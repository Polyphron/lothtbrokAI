using System;
using System.Collections.Generic;
using System.Data.SQLite;

namespace LothbrokAI.Memory
{
    /// <summary>
    /// Semantic vector search over the LothbrokAI memory index.
    ///
    /// DESIGN: Pure in-process cosine similarity — no external vector DB.
    /// For a mod-scale dataset (hundreds to low thousands of memories),
    /// linear scan over stored float[] is fast enough (sub-millisecond).
    ///
    /// Hybrid scoring combines:
    ///   semantic   — cosine similarity between query and memory vectors
    ///   recency    — exponential decay by game-days since memory was stored
    ///   same_npc   — bonus for memories from the active NPC
    ///
    /// Falls back to TF-IDF (via MemoryEngine) when no vectors are available.
    /// </summary>
    public static class VectorIndex
    {
        // ================================================================
        // WRITE
        // ================================================================

        /// <summary>
        /// Upsert a memory node into the SQLite index.
        /// Called from MemoryEngine.Store() after embedding is obtained.
        /// </summary>
        public static void Upsert(
            string id, string npcId, string npcName,
            string text, float[] vector, List<string> tags,
            int gameDay)
        {
            if (!LothbrokDatabase.IsOpen) return;

            byte[] vectorBlob = vector != null ? SerializeVector(vector) : null;
            string tagsJson = tags != null
                ? Newtonsoft.Json.JsonConvert.SerializeObject(tags)
                : "[]";

            using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO memories
                        (id, npc_id, npc_name, text, vector, tags, game_day, created_at, salience)
                    VALUES
                        (@id, @npcId, @npcName, @text, @vector, @tags, @gameDay, @createdAt, 1.0)";

                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@npcId", npcId);
                cmd.Parameters.AddWithValue("@npcName", npcName);
                cmd.Parameters.AddWithValue("@text", text);
                cmd.Parameters.AddWithValue("@vector", (object)vectorBlob ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tags", tagsJson);
                cmd.Parameters.AddWithValue("@gameDay", gameDay);
                cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
        }

        // ================================================================
        // SEARCH
        // ================================================================

        /// <summary>
        /// Semantic memory retrieval. Returns top-K most relevant memory texts.
        ///
        /// When cross-NPC retrieval is enabled (config), searches all NPCs.
        /// Otherwise scoped to the active NPC only.
        /// </summary>
        public static List<string> Search(
            float[] queryVector,
            string activeNpcId,
            int currentGameDay,
            int topK)
        {
            if (!LothbrokDatabase.IsOpen || queryVector == null)
                return new List<string>();

            var config = API.LothbrokConfig.Current;
            string scopeFilter = config.CrossNpcRetrieval
                ? ""
                : $"WHERE npc_id = '{activeNpcId.Replace("'", "''")}'";

            var candidates = new List<(float score, string text)>();

            // Load all candidate vectors - linear scan (fast enough at mod scale)
            using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT npc_id, text, vector, game_day
                    FROM memories
                    {scopeFilter}
                    ORDER BY game_day DESC
                    LIMIT 2000";

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string npcId = reader.GetString(0);
                        string text = reader.GetString(1);
                        int gameDay = reader.GetInt32(3);

                        float[] memVector = null;
                        if (!reader.IsDBNull(2))
                        {
                            byte[] blob = (byte[])reader[2];
                            memVector = DeserializeVector(blob);
                        }

                        // Skip entries without vectors (will be caught by TF-IDF fallback)
                        if (memVector == null || memVector.Length != queryVector.Length)
                            continue;

                        float semantic = CosineSimilarity(queryVector, memVector);

                        // Recency: exponential decay, half-life ~30 game days
                        float dayDelta = Math.Max(0, currentGameDay - gameDay);
                        float recency = (float)Math.Exp(-dayDelta / 30.0);

                        // Same-NPC bonus
                        float sameNpc = npcId == activeNpcId ? 1.0f : 0.0f;

                        float score = semantic * config.SemanticWeight
                                    + recency  * config.RecencyWeight
                                    + sameNpc  * config.SameNpcWeight;

                        candidates.Add((score, text));
                    }
                }
            }

            // Sort descending, take top-K
            candidates.Sort((a, b) => b.score.CompareTo(a.score));

            var results = new List<string>();
            for (int i = 0; i < Math.Min(topK, candidates.Count); i++)
                results.Add(candidates[i].text);

            return results;
        }

        // ================================================================
        // VECTOR UTILITIES
        // ================================================================

        private static float CosineSimilarity(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0f;

            float dot = 0f, magA = 0f, magB = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                dot  += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }

            float denom = (float)(Math.Sqrt(magA) * Math.Sqrt(magB));
            return denom < 1e-8f ? 0f : dot / denom;
        }

        private static byte[] SerializeVector(float[] v)
        {
            // DESIGN: Store as little-endian float array blob.
            // 4 bytes per float. 1536-dim vector = 6144 bytes. Compact enough.
            byte[] bytes = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static float[] DeserializeVector(byte[] blob)
        {
            if (blob == null || blob.Length % 4 != 0) return null;
            float[] v = new float[blob.Length / 4];
            Buffer.BlockCopy(blob, 0, v, 0, blob.Length);
            return v;
        }
    }
}
