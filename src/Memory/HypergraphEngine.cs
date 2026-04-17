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
        /// Extracts concept nodes from tags and creates PAIRWISE hyperedges:
        ///   {Player, CONCEPT_X}   — player discussed concept X
        ///   {NPC, CONCEPT_X}      — concept X associated with this NPC
        ///   {Player, NPC}         — player interacted with NPC
        ///
        /// DESIGN: Pairwise edges (not mega-edges) enable co-occurrence
        /// accumulation. When the player discusses "gold" across 3 different
        /// NPCs, the {Player, CONCEPT_gold} edge reaches activation_count=3.
        /// A mega-edge with ALL tags from one exchange is too specific —
        /// it almost never matches again because any different tag set
        /// produces a different edge hash.
        /// </summary>
        public static void OnExchangeStored(
            string npcId, string npcName,
            string playerNpcId,
            List<string> tags)
        {
            if (!LothbrokDatabase.IsOpen) return;
            if (tags == null || tags.Count == 0) return;

            // Ensure hero nodes exist
            string heroNode = "H_" + npcId;
            string playerNode = "H_" + playerNpcId;
            UpsertNode(heroNode, "hero", npcName);
            UpsertNode(playerNode, "hero", "Player");

            // Create concept nodes
            var conceptNodeIds = new List<string>();
            foreach (string tag in tags)
            {
                string conceptId = "CONCEPT_" + tag.ToLowerInvariant().Replace(" ", "_");
                UpsertNode(conceptId, "concept", tag);
                conceptNodeIds.Add(conceptId);
            }

            // --- PAIRWISE EDGES ---

            // 1. Player ↔ NPC interaction edge (always created)
            UpsertEdge(
                BuildEdgeId(playerNode, heroNode),
                "interaction",
                new List<string> { playerNode, heroNode });

            // 2. Player ↔ each concept (player behavioral pattern)
            foreach (string cid in conceptNodeIds)
            {
                UpsertEdge(
                    BuildEdgeId(playerNode, cid),
                    "player_" + cid.Replace("CONCEPT_", ""),
                    new List<string> { playerNode, cid });
            }

            // 3. NPC ↔ each concept (NPC association)
            foreach (string cid in conceptNodeIds)
            {
                UpsertEdge(
                    BuildEdgeId(heroNode, cid),
                    npcName.ToLowerInvariant() + "_" + cid.Replace("CONCEPT_", ""),
                    new List<string> { heroNode, cid });
            }

            // 4. Context triple: {Player, NPC, Concept} — the specific context
            //    Only for concepts that feel significant (top 2 by occurrence)
            int tripleLimit = Math.Min(2, conceptNodeIds.Count);
            for (int i = 0; i < tripleLimit; i++)
            {
                var tripleNodes = new List<string> { playerNode, heroNode, conceptNodeIds[i] };
                UpsertEdge(
                    BuildEdgeId(tripleNodes),
                    "context_" + conceptNodeIds[i].Replace("CONCEPT_", ""),
                    tripleNodes);
            }
        }

        // ================================================================
        // SPREADING ACTIVATION ON RETRIEVAL
        // ================================================================

        /// <summary>
        /// Given the active context (current NPC + concepts from current message),
        /// find all hyperedges that share nodes with the context.
        ///
        /// DESIGN (Manifold Fold): After finding directly fired edges, we
        /// TRAVERSE the property graph to find secondary activation targets.
        /// If a fired edge involves H_dermot + CONCEPT_gold, and Dermot is
        /// a vassal of Battania, then all Battanian lords who also have
        /// CONCEPT_gold edges get their edges surfaced too.
        ///
        /// The hypergraph provides activation energy.
        /// The property graph provides traversal paths.
        /// Together they form a surface richer than either alone.
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

            // --- PHASE 1: Direct edge activation ---
            // Find pairwise edges where the active node set matches
            var firedEdgeIds = new HashSet<string>();
            var firedHeroNodes = new HashSet<string>();
            var firedConceptNodes = new HashSet<string>();

            using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
            {
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

                        firedEdgeIds.Add(edgeId);

                        var edgeNodeLabels = GetEdgeNodeLabels(edgeId);
                        if (edgeNodeLabels.Count < 2) continue;

                        string chain = FormatContextChain(label, edgeNodeLabels, activationCount);
                        results.Add((activationCount, chain));
                    }
                }
            }

            // Collect hero and concept nodes from fired edges for Phase 2
            foreach (string edgeId in firedEdgeIds)
            {
                foreach (string nodeId in GetEdgeNodeIds(edgeId))
                {
                    if (nodeId.StartsWith("H_")) firedHeroNodes.Add(nodeId);
                    else if (nodeId.StartsWith("CONCEPT_")) firedConceptNodes.Add(nodeId);
                }
            }

            // --- PHASE 2: Property graph traversal (the manifold fold) ---
            // For each hero in a fired edge, find structurally connected heroes
            // (same clan, same kingdom) and check if THEY have edges with the
            // same fired concepts. This is how reputation propagates.
            if (firedConceptNodes.Count > 0 && firedHeroNodes.Count > 0)
            {
                var secondaryHeroes = FindStructuralNeighbors(activeNpcId, firedHeroNodes);

                // Check if these secondary heroes have edges with fired concepts
                foreach (string secondaryHero in secondaryHeroes)
                {
                    if (activeNodes.Contains(secondaryHero)) continue; // skip self
                    if (firedHeroNodes.Contains(secondaryHero)) continue; // already fired

                    foreach (string concept in firedConceptNodes)
                    {
                        string candidateEdgeId = BuildEdgeId(secondaryHero, concept);

                        using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
                        {
                            cmd.CommandText = @"
                                SELECT label, activation_count FROM hg_edges
                                WHERE id = @id AND activation_count >= @min";
                            cmd.Parameters.AddWithValue("@id", candidateEdgeId);
                            cmd.Parameters.AddWithValue("@min", config.HyperedgeMinActivations);

                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    string label = reader.GetString(0);
                                    int count = reader.GetInt32(1);
                                    var labels = GetEdgeNodeLabels(candidateEdgeId);
                                    string chain = "⟵ " + FormatContextChain(
                                        label, labels, count);
                                    results.Add((count, chain));
                                }
                            }
                        }
                    }

                    // Cap secondary results to avoid prompt bloat
                    if (results.Count >= config.MaxContextChains * 2) break;
                }
            }

            // Increment activation count for all directly fired edges
            if (firedEdgeIds.Count > 0)
                IncrementActivations(activeNodes, config.HyperedgeMinActivations);

            return results.Select(r => r.chain).ToList();
        }

        /// <summary>
        /// Find heroes structurally connected to the given heroes via
        /// Bannerlord's clan/kingdom hierarchy.
        ///
        /// DESIGN: This IS the manifold fold. The hypergraph provides
        /// the activation energy (which concepts fired), and this method
        /// traverses the real-world game structure to find secondary targets.
        /// We query LIVE game state (not cached SQLite) because relationships
        /// change dynamically (wars, defections, marriages).
        /// </summary>
        private static HashSet<string> FindStructuralNeighbors(
            string activeNpcId,
            HashSet<string> seedHeroNodes)
        {
            var neighbors = new HashSet<string>();

            try
            {
                var campaign = TaleWorlds.CampaignSystem.Campaign.Current;
                if (campaign == null) return neighbors;

                // For each fired hero, find their clan/kingdom mates
                foreach (string heroNode in seedHeroNodes)
                {
                    string heroId = heroNode.Replace("H_", "");
                    var hero = TaleWorlds.CampaignSystem.Hero.FindFirst(
                        h => h.StringId == heroId);
                    if (hero?.Clan == null) continue;

                    // Same clan = strong structural connection
                    foreach (var clanmate in hero.Clan.Heroes)
                    {
                        if (clanmate.IsAlive && clanmate.StringId != activeNpcId)
                            neighbors.Add("H_" + clanmate.StringId);
                    }

                    // Same kingdom = weaker but still meaningful
                    if (hero.Clan.Kingdom != null)
                    {
                        foreach (var vassalClan in hero.Clan.Kingdom.Clans)
                        {
                            if (vassalClan.Leader != null
                                && vassalClan.Leader.IsAlive
                                && vassalClan.Leader.StringId != activeNpcId)
                            {
                                neighbors.Add("H_" + vassalClan.Leader.StringId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LothbrokSubModule.Log("[HypergraphEngine] Structural neighbor lookup failed: "
                    + ex.Message, TaleWorlds.Library.Debug.DebugColor.Yellow);
            }

            return neighbors;
        }

        /// <summary>Get raw node IDs (not labels) for an edge.</summary>
        private static List<string> GetEdgeNodeIds(string edgeId)
        {
            var ids = new List<string>();
            using (var cmd = LothbrokDatabase.GetConnection().CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT node_id FROM hg_edge_nodes
                    WHERE edge_id = @edgeId";
                cmd.Parameters.AddWithValue("@edgeId", edgeId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        ids.Add(reader.GetString(0));
                }
            }
            return ids;
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
            // Example: "player_gold [Player, gold] (×5)"
            string nodes = string.Join(", ", nodeLabels.Take(5));
            return $"{label} [{nodes}] (×{activationCount})";
        }

        /// <summary>Canonical 2-node edge ID (pairwise).</summary>
        private static string BuildEdgeId(string nodeA, string nodeB)
        {
            // Sorted so {A,B} and {B,A} are the same edge
            string first = string.CompareOrdinal(nodeA, nodeB) <= 0 ? nodeA : nodeB;
            string second = first == nodeA ? nodeB : nodeA;
            string combined = first + "|" + second;
            return "HE_" + Math.Abs(combined.GetHashCode()).ToString("X8");
        }

        /// <summary>Canonical n-node edge ID (for context triples).</summary>
        private static string BuildEdgeId(List<string> nodeIds)
        {
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
