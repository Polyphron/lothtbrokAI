using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace LothbrokAI.Core
{
    /// <summary>
    /// Implements functions that the LLM can call mid-conversation to look up
    /// deeper game state dynamically without front-loading massive context.
    /// DESIGN: These tools run on a background thread. They strictly perform READ-ONLY 
    /// lookups on game state. Safe to execute asynchronously.
    /// </summary>
    public static class ToolRegistry
    {
        public static JArray GetAvailableTools()
        {
            return new JArray
            {
                CreateOmniTool("query_character", "Get information about a specific lord or lady.", new JObject
                {
                    ["hero_name"] = new JObject { ["type"] = "string", ["description"] = "The exact name of the hero." },
                    ["query_type"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray { "relationships", "wealth_and_holdings", "location_and_army", "traits" }
                    }
                }),
                
                CreateOmniTool("query_settlement", "Get information about a specific town, castle, or village.", new JObject
                {
                    ["settlement_name"] = new JObject { ["type"] = "string", ["description"] = "The exact name of the settlement." },
                    ["query_type"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray { "economy", "garrison_and_defense", "ownership" }
                    }
                }),
                
                CreateOmniTool("query_kingdom", "Get information about an entire kingdom or empire.", new JObject
                {
                    ["kingdom_name"] = new JObject { ["type"] = "string", ["description"] = "The exact name of the kingdom." },
                    ["query_type"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray { "wars_and_diplomacy", "strength_and_clans", "policies" }
                    }
                }),
                
                CreateOmniTool("execute_game_action", "Queue a physical game mechanic or diplomatic action to execute when the conversation ends.", new JObject
                {
                    ["action_type"] = new JObject { ["type"] = "string", ["description"] = "The registered handler type (e.g. declare_war, grant_favor, transfer_fief)." },
                    ["target"] = new JObject { ["type"] = "string", ["description"] = "Optional name of the target Kingdom or Hero." },
                    ["string_payload"] = new JObject { ["type"] = "string", ["description"] = "Optional text payload (like a rumor or quest description)." },
                    ["settlement_id"] = new JObject { ["type"] = "string", ["description"] = "Optional name of a settlement." },
                    ["value"] = new JObject { ["type"] = "number", ["description"] = "Optional magnitude (1-10) or gold amount." }
                })
            };
        }

        private static JObject CreateOmniTool(string name, string description, JObject properties)
        {
            var requiredParams = properties.Properties().Select(p => p.Name).ToArray();
            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = new JArray(requiredParams)
                    }
                }
            };
        }

        public static string ExecuteTool(string toolName, string arguments)
        {
            try
            {
                var args = JObject.Parse(arguments);
                
                if (toolName == "query_character")
                    return QueryCharacter(args["hero_name"]?.ToString() ?? "", args["query_type"]?.ToString() ?? "");
                if (toolName == "query_settlement")
                    return QuerySettlement(args["settlement_name"]?.ToString() ?? "", args["query_type"]?.ToString() ?? "");
                if (toolName == "query_kingdom")
                    return QueryKingdom(args["kingdom_name"]?.ToString() ?? "", args["query_type"]?.ToString() ?? "");
                if (toolName == "execute_game_action")
                    return QueueAction(
                        args["action_type"]?.ToString() ?? "",
                        args["target"]?.ToString(),
                        args["string_payload"]?.ToString(),
                        args["settlement_id"]?.ToString(),
                        args["value"]?.Value<float>() ?? 0f);
                
                return "Error: Unknown tool name.";
            }
            catch (Exception ex)
            {
                return $"Error executing tool {toolName}: {ex.Message}";
            }
        }

        private static string QueryCharacter(string name, string type)
        {
            var hero = Campaign.Current.AliveHeroes.FirstOrDefault(h => 
                h.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase) ||
                h.Name.ToString().Contains(name));
            
            if (hero == null) return $"Hero '{name}' not found or is dead.";

            if (type == "relationships")
            {
                var friends = Campaign.Current.AliveHeroes.Where(h => h != hero && h.GetRelation(hero) >= 20).Select(h => h.Name.ToString()).Take(5);
                var enemies = Campaign.Current.AliveHeroes.Where(h => h != hero && h.GetRelation(hero) <= -20).Select(h => h.Name.ToString()).Take(5);
                return $"Friends: {(friends.Any() ? string.Join(", ", friends) : "None")}\nEnemies: {(enemies.Any() ? string.Join(", ", enemies) : "None")}\nClan: {hero.Clan?.Name}\nSpouse: {hero.Spouse?.Name}\nLiege: {hero.Clan?.Kingdom?.Leader?.Name}";
            }
            if (type == "wealth_and_holdings")
            {
                var fiefs = hero.Clan?.Settlements?.Select(s => s.Name.ToString()) ?? new List<string>();
                return $"Gold: {hero.Gold}\nClan Wealth: {hero.Clan?.Leader?.Gold} (Leader)\nFiefs owned by clan: {(fiefs.Any() ? string.Join(", ", fiefs) : "None")}\nInfluence: {hero.Clan?.Influence}";
            }
            if (type == "location_and_army")
            {
                if (hero.IsPrisoner) return $"Status: Prisoner at {hero.PartyBelongedToAsPrisoner?.Name}.";
                if (hero.PartyBelongedTo != null) return $"Status: Leading party '{hero.PartyBelongedTo.Name}' with {hero.PartyBelongedTo.MemberRoster.TotalManCount} troops. Location: Near {hero.CurrentSettlement?.Name?.ToString() ?? "open map"}.";
                if (hero.CurrentSettlement != null) return $"Status: Resting in {hero.CurrentSettlement.Name}.";
                return "Status: Traveling or location unknown.";
            }
            if (type == "traits")
            {
                // Traits like Honor, Mercy, Valor, Calculating
                int honor = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Honor);
                int mercy = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Mercy);
                int valor = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Valor);
                int calculating = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Calculating);
                return $"Honor: {honor}, Mercy: {mercy}, Valor: {valor}, Calculating: {calculating}";
            }

            return "Invalid query type.";
        }

        private static string QuerySettlement(string name, string type)
        {
            var s = Settlement.All.FirstOrDefault(x => 
                x.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase) ||
                x.Name.ToString().Contains(name));
            
            if (s == null) return $"Settlement '{name}' not found.";

            if (type == "economy")
            {
                if (s.Town != null) return $"Prosperity: {s.Town.Prosperity}\nLoyalty: {s.Town.Loyalty}\nSecurity: {s.Town.Security}\nFood Stocks: {s.Town.FoodStocks}";
                if (s.Village != null) return $"Hearth: {s.Village.Hearth}\nPrimary Production: {s.Village.VillageType.PrimaryProduction.Name}";
            }
            if (type == "garrison_and_defense")
            {
                if (s.Town != null) return $"Garrison: {s.Town.GarrisonParty?.MemberRoster.TotalManCount ?? 0} troops.\nMilitia: {s.Town.Militia}\nWall Level: {s.Town.GetWallLevel()}\nUnder Siege: {s.IsUnderSiege}";
                if (s.Village != null) return $"Militia: {s.Village.Militia}\nRaided: {s.IsRaided}";
            }
            if (type == "ownership")
            {
                return $"Owner: {s.OwnerClan?.Leader?.Name}\nClan: {s.OwnerClan?.Name}\nKingdom: {s.MapFaction?.Name}";
            }

            return "Invalid query type or missing data.";
        }

        private static string QueryKingdom(string name, string type)
        {
            var k = Campaign.Current.Kingdoms.FirstOrDefault(x => 
                x.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase) ||
                x.Name.ToString().Contains(name));
            
            if (k == null) return $"Kingdom '{name}' not found.";

            if (type == "wars_and_diplomacy")
            {
                var enemies = Campaign.Current.Kingdoms.Where(e => e.IsAtWarWith(k)).Select(e => e.Name.ToString());
                return $"At War With: {(enemies.Any() ? string.Join(", ", enemies) : "None")}";
            }
            if (type == "strength_and_clans")
            {
                return $"Total Fiefs: {k.Fiefs.Count}\nTotal Clans: {k.Clans.Count}\nTotal Heroes: {k.Heroes.Count}\nRuler: {k.Leader?.Name}";
            }
            if (type == "policies")
            {
                var policies = k.ActivePolicies.Select(p => p.Name.ToString());
                return $"Active Policies: {(policies.Any() ? string.Join(", ", policies) : "None")}";
            }

            return "Invalid query type.";
        }

        private static string QueueAction(string actionType, string target, string payload, string settlementId, float value)
        {
            var npc = Hero.OneToOneConversationHero;
            var player = Hero.MainHero;
            
            if (npc == null || player == null) return "Error: Not currently in a hero conversation.";

            var action = new NpcAction
            {
                Type = actionType,
                Target = target,
                ItemName = payload,
                SettlementId = settlementId,
                Value = value,
                GoldAmount = (int)value
            };

            // Queue for Main Thread execution
            ActionEngine.MainThreadQueue.Enqueue(() => {
                var list = new List<NpcAction> { action };
                ActionEngine.ProcessActions(list, npc, player);
            });

            return $"Action '{actionType}' was accepted and queued for execution. The game state will mutate immediately after this dialogue turn. Narrate the outcome as if it just occurred.";
        }
    }
}
