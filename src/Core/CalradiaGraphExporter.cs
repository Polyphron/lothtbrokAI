using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace LothbrokAI.Core
{
    public static class CalradiaGraphExporter
    {
        private class GraphNode
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("properties")] public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        }

        private class GraphEdge
        {
            [JsonProperty("source")] public string SourceId { get; set; }
            [JsonProperty("target")] public string TargetId { get; set; }
            [JsonProperty("type")] public string Type { get; set; }
            [JsonProperty("properties")] public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
        }

        private class GraphExport
        {
            [JsonProperty("nodes")] public List<GraphNode> Nodes { get; set; } = new List<GraphNode>();
            [JsonProperty("edges")] public List<GraphEdge> Edges { get; set; } = new List<GraphEdge>();
        }

        /// <summary>
        /// Exports the entirety of the live Calradia state into a generic Graph format.
        /// Extracts Heroes, Clans, Kingdoms, and Settlements to empower Phase 8 LLM Cypher Queries.
        /// </summary>
        public static string ExportGraph(string outputPath)
        {
            var graph = new GraphExport();

            try
            {
                // ==========================================
                // 1. EXTRACT KINGDOMS
                // ==========================================
                foreach (var kingdom in Campaign.Current.Kingdoms)
                {
                    if (kingdom.IsEliminated) continue;
                    
                    graph.Nodes.Add(new GraphNode
                    {
                        Id = "K_" + kingdom.StringId,
                        Type = "Kingdom",
                        Properties = { ["name"] = kingdom.Name.ToString() }
                    });

                    // Edges: Wars
                    foreach (var enemy in Campaign.Current.Kingdoms)
                    {
                        if (enemy != kingdom && !enemy.IsEliminated && kingdom.IsAtWarWith(enemy))
                        {
                            graph.Edges.Add(new GraphEdge
                            {
                                SourceId = "K_" + kingdom.StringId,
                                TargetId = "K_" + enemy.StringId,
                                Type = "AT_WAR_WITH"
                            });
                        }
                    }
                }

                // ==========================================
                // 2. EXTRACT CLANS
                // ==========================================
                foreach (var clan in Clan.All)
                {
                    if (clan.IsEliminated) continue;

                    graph.Nodes.Add(new GraphNode
                    {
                        Id = "C_" + clan.StringId,
                        Type = "Clan",
                        Properties =
                        {
                            ["name"] = clan.Name.ToString(),
                            ["tier"] = clan.Tier,
                            ["is_minor"] = clan.IsMinorFaction
                        }
                    });

                    // Edge: Vassalage
                    if (clan.Kingdom != null && !clan.Kingdom.IsEliminated)
                    {
                        graph.Edges.Add(new GraphEdge
                        {
                            SourceId = "C_" + clan.StringId,
                            TargetId = "K_" + clan.Kingdom.StringId,
                            Type = "VASSAL_OF"
                        });
                    }
                }

                // ==========================================
                // 3. EXTRACT HEROES
                // ==========================================
                foreach (var hero in Campaign.Current.AliveHeroes)
                {
                    graph.Nodes.Add(new GraphNode
                    {
                        Id = "H_" + ContextAssembler.GetNpcId(hero),
                        Type = "Hero",
                        Properties =
                        {
                            ["name"] = hero.Name.ToString(),
                            ["occupation"] = hero.Occupation.ToString(),
                            ["level"] = hero.Level
                        }
                    });

                    // Edge: Clan Membership
                    if (hero.Clan != null && !hero.Clan.IsEliminated)
                    {
                        graph.Edges.Add(new GraphEdge
                        {
                            SourceId = "H_" + ContextAssembler.GetNpcId(hero),
                            TargetId = "C_" + hero.Clan.StringId,
                            Type = "BELONGS_TO"
                        });
                    }

                    // Edges: Relationships
                    foreach (var target in Campaign.Current.AliveHeroes)
                    {
                        if (hero == target) continue;
                        
                        int relation = hero.GetRelation(target);
                        if (relation >= 30)
                        {
                            graph.Edges.Add(new GraphEdge
                            {
                                SourceId = "H_" + ContextAssembler.GetNpcId(hero),
                                TargetId = "H_" + ContextAssembler.GetNpcId(target),
                                Type = "IS_FRIEND_OF",
                                Properties = { ["relation"] = relation }
                            });
                        }
                        else if (relation <= -30)
                        {
                            graph.Edges.Add(new GraphEdge
                            {
                                SourceId = "H_" + ContextAssembler.GetNpcId(hero),
                                TargetId = "H_" + ContextAssembler.GetNpcId(target),
                                Type = "IS_ENEMY_OF",
                                Properties = { ["relation"] = relation }
                            });
                        }
                    }
                }

                // ==========================================
                // 4. EXTRACT SETTLEMENTS
                // ==========================================
                foreach (var settlement in Settlement.All)
                {
                    if (settlement.IsTown || settlement.IsCastle || settlement.IsVillage)
                    {
                        string sType = settlement.IsTown ? "Town" : settlement.IsCastle ? "Castle" : "Village";
                        
                        graph.Nodes.Add(new GraphNode
                        {
                            Id = "S_" + settlement.StringId,
                            Type = "Settlement",
                            Properties =
                            {
                                ["name"] = settlement.Name.ToString(),
                                ["settlement_type"] = sType
                            }
                        });

                        // Edge: Ownership
                        if (settlement.OwnerClan != null && !settlement.OwnerClan.IsEliminated)
                        {
                            graph.Edges.Add(new GraphEdge
                            {
                                SourceId = "S_" + settlement.StringId,
                                TargetId = "C_" + settlement.OwnerClan.StringId,
                                Type = "OWNED_BY"
                            });
                        }
                    }
                }

                // Serialize and export
                string json = JsonConvert.SerializeObject(graph, Formatting.None); // Minified for API
                
                if (!string.IsNullOrEmpty(outputPath))
                {
                    File.WriteAllText(outputPath, JsonConvert.SerializeObject(graph, Formatting.Indented));
                    LothbrokSubModule.Log($"Graph Map Exported: {graph.Nodes.Count} Nodes, {graph.Edges.Count} Edges", TaleWorlds.Library.Debug.DebugColor.Green);
                }
                return json;
            }
            catch (Exception ex)
            {
                LothbrokSubModule.LogError("GraphExportFailed", ex);
                return JsonConvert.SerializeObject(new { error = ex.Message });
            }
        }
    }
}
