using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace LothbrokAI.Memory
{
    /// <summary>
    /// Hypergraph context engine for LothbrokAI memory v2.
    ///
    /// DESIGN: Captures n-ary co-occurrence relationships between concepts
    /// that emerge from conversation. Unlike a simple binary graph (A → B),
    /// a hyperedge can represent {Hero, Concept, Kingdom, Emotion} tuples —
    /// higher-order context that binary edges cannot encode.
    ///
    /// Example hyperedge:
    ///   nodes: [H_dermot, H_playercharacter, CONCEPT_gold, CONCEPT_bribery]
    ///   label: "bribery_context"
    ///   activation_count: 3
    ///
    /// When the current conversation activates two or more nodes in an edge,
    /// that edge "fires" — contributing its full context to the prompt.
    /// Edge strength is derived from how often it has fired (co-occurrence
    /// frequency), not arbitrarily decayed weights.
    ///
    /// This is deliberately NOT the full ASL architecture. It is an
    /// explainable, public-facing proof-of-concept for context-aware
    /// memory engineering.
    /// </summary>
    public static class HypergraphEngine
    {
        // ================================================================
        // STORE EXCHANGE → BUILD HYPEREDGES
        // ================================================================

        /// <summary>
        /// Called after each conversation exchange.
        /// Extracts concept nodes from tags and creates/strengthens hyperedges
        /// linking the NPC, player, and any activated concepts.
        /// </summary>
        public static void OnExchangeStored(
            string npcId, string npcName,
            string playerNpcId,
            List<string> tags)
        {
            if (!LothbrokDatabase.IsOpen) return;
            if (tags == null || tags.Count == 0) return;

            // Ensure hero nodes exist
            UpsertNode("H_" + npcId, "hero", npcName);
            UpsertNode("H_" + playerNpcId, "hero", "Player");

            // Create concept nodes from tags
            var conceptNodeIds = new List<string>();
            foreach (string tag in tags)
            {
                string conceptId = "CONCEPT_" + tag.ToLowerInvariant().Replace(" ", "_");
                UpsertNode(conceptId, "concept", tag);
                conceptNodeIds.Add(conceptId);
            }

            // Only build edges when we have enough concept signal
            if (conceptNodeIds.Count == 0) return;

            // Build hyperedge: {NPC, Player, concepts...}
            // Group concepts into one edge per exchange — captures the full context
            // of this moment rather than isolated pairwise edges.
            var edgeNodes = new List<string> { "H_" + npcId, "H_" + playerNpcId };
            edgeNodes.AddRange(conceptNodeIds);

            string edgeLabel = BuildEdgeLabel(tags);
            string edgeId = BuildEdgeId(edgeNodes);

            UpsertEdge(edgeId, edgeLabel, edgeNodes);
        }

        // ================================================================
        // SPREADING ACTIVATION ON RETRIEVAL
        // ================================================================

        /// <summary>
        /// Given the active context (current NPC + concepts from current message),
        /// find all hyperedges that share 2+ nodes with the context.
        /// Returns those edges as natural language context chain strings
        /// for injection into the prompt.
        /// </summary>
        public static List<string> GetActivatedContextChains(
            string activeNpcId,
            List<string> currentMessageTags)
        {
            if (!LothbrokDatabase.IsOpen) return new List<string>();

            var config = API.LothbrokConfig.Current;
            var results = new List<(int activationCount, string chain)>();

            // Build active node set from current context
            var activeNodes = new HashSet<string> { "H_" + activeNpcId };
            foreach (string tag in currentMessageTags ?? new List<string>())
            {
                activeNodes.Add("CONCEPT_" + tag.ToLowerInvariant().Replace(" ", "_"));
            }

            if (activeNodes.Count < 2) return new List<string>();

            // Find edges where at least 2 of their nodes are in the active set
            using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
            {
                // Get edge IDs with matching node count
                string nodeList = string.Join(",",
                    activeNodes.Select(n => $"'{n.Replace("'", "''")}'")); 

                cmd.CommandText = $@"
                    SELECT e.id, e.label, e.activation_count
                    FROM hg_edges e
                    JOIN hg_edge_nodes en ON e.id = en.edge_id
                    WHERE en.node_id IN ({nodeList})
                    GROUP BY e.id
                    HAVING COUNT(DISTINCT en.node_id) >= 2
                      AND e.activation_count >= @minActivations
                    ORDER BY e.activation_count DESC, e.last_activated DESC
                    LIMIT @maxChains";

                cmd.Parameters.AddWithValue("@minActivations", config.HyperedgeMinActivations);
                cmd.Parameters.AddWithValue("@maxChains", config.MaxContextChains);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string edgeId = reader.GetString(0);
                        string label = reader.GetString(1);
                        int activationCount = reader.GetInt32(2);

                        // Get all nodes for this edge to build a readable context chain
                        var edgeNodeLabels = GetEdgeNodeLabels(edgeId);
                        if (edgeNodeLabels.Count < 2) continue;

                        string chain = FormatContextChain(label, edgeNodeLabels, activationCount);
                        results.Add((activationCount, chain));
                    }
                }
            }

            // Increment activation count for all fired edges
            if (results.Count > 0)
                IncrementActivations(activeNodes, config.HyperedgeMinActivations);

            return results.Select(r => r.chain).ToList();
        }

        // ================================================================
        // NPCWORLD NODES (from CalradiaGraphExporter integration)
        // ================================================================

        /// <summary>
        /// Register world-state nodes (kingdoms, clans) into the hypergraph.
        /// Called once per conversation from ContextAssembler.
        /// Allows world-state nodes to participate in concept spreading.
        /// </summary>
        public static void RegisterWorldNodes(string graphJson)
        {
            if (!LothbrokDatabase.IsOpen || string.IsNullOrEmpty(graphJson)) return;

            try
            {
                var graph = JsonConvert.DeserializeObject<WorldGraphSnapshot>(graphJson);
                if (graph?.Nodes == null) return;

                foreach (var node in graph.Nodes)
                {
                    // Only register kingdoms and clans — heroes registered per-conversation
                    if (node.Type == "Kingdom" || node.Type == "Clan")
                    {
                        string nodeId = node.Id; // Already prefixed: "K_xxx", "C_xxx"
                        UpsertNode(nodeId, node.Type.ToLower(), node.Label);
                    }
                }
            }
            catch (Exception ex)
            {
                LothbrokSubModule.Log("[HypergraphEngine] WorldNode registration failed: " + ex.Message,
                    TaleWorlds.Library.Debug.DebugColor.Red);
            }
        }

        // ================================================================
        // DB HELPERS
        // ================================================================

        private static void UpsertNode(string id, string type, string label)
        {
            using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO hg_nodes (id, node_type, label)
                    VALUES (@id, @type, @label)";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@label", label);
                cmd.ExecuteNonQuery();
            }
        }

        private static void UpsertEdge(string edgeId, string label, List<string> nodeIds)
        {
            string now = DateTime.UtcNow.ToString("o");

            using (var tx = LothbrokDatabase.GetConnection().BeginTransaction())
            {
                try
                {
                    // Insert or increment activation count
                    using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
                            INSERT INTO hg_edges (id, label, activation_count, last_activated, created_at)
                            VALUES (@id, @label, 1, @now, @now)
                            ON CONFLICT(id) DO UPDATE SET
                                activation_count = activation_count + 1,
                                last_activated = @now";
                        cmd.Parameters.AddWithValue("@id", edgeId);
                        cmd.Parameters.AddWithValue("@label", label);
                        cmd.Parameters.AddWithValue("@now", now);
                        cmd.ExecuteNonQuery();
                    }

                    // Insert edge-node junction rows
                    foreach (string nodeId in nodeIds)
                    {
                        using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText = @"
                                INSERT OR IGNORE INTO hg_edge_nodes (edge_id, node_id)
                                VALUES (@edgeId, @nodeId)";
                            cmd.Parameters.AddWithValue("@edgeId", edgeId);
                            cmd.Parameters.AddWithValue("@nodeId", nodeId);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        private static void IncrementActivations(HashSet<string> activeNodes, int minActivations)
        {
            string nodeList = string.Join(",",
                activeNodes.Select(n => $"'{n.Replace("'", "''")}'")); 

            using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
            {
                cmd.CommandText = $@"
                    UPDATE hg_edges SET
                        activation_count = activation_count + 1,
                        last_activated = @now
                    WHERE id IN (
                        SELECT e.id FROM hg_edges e
                        JOIN hg_edge_nodes en ON e.id = en.edge_id
                        WHERE en.node_id IN ({nodeList})
                        GROUP BY e.id
                        HAVING COUNT(DISTINCT en.node_id) >= 2
                          AND e.activation_count >= @min
                    )";
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@min", minActivations);
                cmd.ExecuteNonQuery();
            }
        }

        private static List<string> GetEdgeNodeLabels(string edgeId)
        {
            var labels = new List<string>();
            using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT n.label FROM hg_nodes n
                    JOIN hg_edge_nodes en ON n.id = en.node_id
                    WHERE en.edge_id = @edgeId";
                cmd.Parameters.AddWithValue("@edgeId", edgeId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        labels.Add(reader.GetString(0));
                }
            }
            return labels;
        }

        // ================================================================
        // FORMATTING
        // ================================================================

        private static string FormatContextChain(
            string label, List<string> nodeLabels, int activationCount)
        {
            // DESIGN: Format as a compact, readable context hint for the LLM.
            // Example: "bribery_context [Dermot, Player, gold, trust] (×3)"
            string nodes = string.Join(", ", nodeLabels.Take(5));
            return $"{label} [{nodes}] (×{activationCount})";
        }

        private static string BuildEdgeLabel(List<string> tags)
        {
            // Label is the first 2 content tags joined — descriptive but short
            var content = tags.Take(2).Select(t => t.ToLowerInvariant().Replace(" ", "_"));
            return string.Join("_", content) + "_context";
        }

        private static string BuildEdgeId(List<string> nodeIds)
        {
            // Canonical edge ID: sorted node IDs joined — ensures same set = same edge
            var sorted = new List<string>(nodeIds);
            sorted.Sort();
            string combined = string.Join("|", sorted);
            return "HE_" + Math.Abs(combined.GetHashCode()).ToString("X8");
        }

        // ================================================================
        // HELPER TYPES
        // ================================================================

        private class WorldGraphSnapshot
        {
            [JsonProperty("nodes")] public List<WorldNode> Nodes { get; set; }
        }

        private class WorldNode
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("properties")] public Dictionary<string, object> Properties { get; set; }

            public string Label => Properties != null && Properties.ContainsKey("name")
                ? Properties["name"].ToString()
                : Id;
        }
    }
}
