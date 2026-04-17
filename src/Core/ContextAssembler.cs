using System;
using System.Linq;
using System.Text;
using LothbrokAI.API;
using LothbrokAI.Memory;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace LothbrokAI.Core
{
    /// <summary>
    /// Assembles context for the PromptBuilder from game state and memory.
    /// 
    /// DESIGN: This is the bridge between Bannerlord's game state and our
    /// prompt system. It reads from the game's live data (Hero, Settlement,
    /// Kingdom, etc.) and from MemoryEngine, formatting everything into
    /// strings that PromptBuilder can token-budget.
    /// 
    /// World context is FILTERED — we don't dump the entire world state.
    /// Only relevant information (nearby NPCs, recent events, faction status)
    /// is included. Designed for 80+ kingdoms (Separatism).
    /// </summary>
    public static class ContextAssembler
    {
        /// <summary>
        /// Assemble complete context for an NPC conversation.
        /// Returns a BuiltPrompt ready for APIRouter.
        /// </summary>
        public static BuiltPrompt AssembleForConversation(
            Hero npc, Hero player, string playerMessage)
        {
            string npcId = GetNpcId(npc);
            string npcName = npc.Name.ToString();
            string playerName = player.Name.ToString();

            // Retrieve memory for this NPC
            var memory = MemoryEngine.Retrieve(npcId, npcName, playerMessage);

            // Build world context
            string worldContext = BuildWorldContext(npc, player);

            // Build relationship description
            string relationship = BuildRelationshipContext(npc, player, memory.Metadata);

            // Build RP items context
            string rpItems = BuildInventoryContext(player);

            // Assemble via PromptBuilder
            return PromptBuilder.Build(
                npcName: npcName,
                npcPersonality: memory.Personality,
                npcBackstory: memory.Backstory,
                speechQuirks: memory.SpeechQuirks,
                relationshipState: relationship,
                memorySummary: memory.Summary,
                retrievedMemories: memory.RelevantMemories,
                recentMessages: memory.RecentMessages,
                worldContext: worldContext,
                rpItems: rpItems,
                playerName: playerName,
                playerMessage: playerMessage,
                emotionalState: memory.Metadata.EmotionalState);
        }

        // ================================================================
        // WORLD CONTEXT
        // ================================================================

        /// <summary>
        /// Build filtered world context relevant to this conversation.
        /// DESIGN: Capped at ~400 tokens. Only relevant info.
        /// </summary>
        private static string BuildWorldContext(Hero npc, Hero player)
        {
            var sb = new StringBuilder();

            // Current location
            string location = GetLocationDescription(npc);
            sb.AppendLine("Location: " + location);

            // Settlement status (own/allied/enemy/neutral)
            if (npc.CurrentSettlement != null)
            {
                sb.AppendLine("Settlement status: " +
                    GetSettlementStatus(npc.CurrentSettlement, player));
            }

            // NPC's faction and attitude to player's faction
            if (npc.Clan != null && npc.Clan.Kingdom != null)
            {
                sb.AppendLine("NPC's kingdom: " + npc.Clan.Kingdom.Name);

                if (player.Clan != null && player.Clan.Kingdom != null)
                {
                    var relation = GetFactionRelation(
                        npc.Clan.Kingdom, player.Clan.Kingdom);
                    sb.AppendLine("Faction relation: " + relation);
                }
            }

            // Nearby parties (limited to 3 most relevant)
            string nearbyInfo = GetNearbyInfo(npc);
            if (!string.IsNullOrEmpty(nearbyInfo))
                sb.AppendLine("Nearby: " + nearbyInfo);

            // Active wars (only involving NPC's kingdom, max 3)
            string warInfo = GetActiveWars(npc);
            if (!string.IsNullOrEmpty(warInfo))
                sb.AppendLine("Active wars: " + warInfo);

            // NPC-specific context
            if (npc.GovernorOf != null)
                sb.AppendLine("Governs: " + npc.GovernorOf.Name);

            if (npc.PartyBelongedTo != null && npc.PartyBelongedTo.Army != null)
                sb.AppendLine("In army led by: " +
                    npc.PartyBelongedTo.Army.LeaderParty.LeaderHero.Name);

            if (npc.IsPrisoner)
                sb.AppendLine("STATUS: Prisoner");

            return sb.ToString();
        }

        // ================================================================
        // RELATIONSHIP CONTEXT
        // ================================================================

        private static string BuildRelationshipContext(
            Hero npc, Hero player, NpcMetadata metadata)
        {
            var sb = new StringBuilder();

            // Game relation
            int gameRelation = npc.GetRelation(player);
            sb.AppendLine("Relation: " + gameRelation + " (" +
                GetRelationLabel(gameRelation) + ")");

            // Trust level
            sb.AppendLine("Trust: " + (metadata.TrustLevel * 100).ToString("F0") + "%");

            // Romance level
            if (metadata.RomanceLevel > 0)
                sb.AppendLine("Romance: " + (metadata.RomanceLevel * 100).ToString("F0") + "%");

            // Interactions
            sb.AppendLine("Times met: " + metadata.InteractionCount);

            // Spine relationships (Family)
            if (npc.Father != null && npc.Father.IsAlive)
                sb.AppendLine("Father: " + npc.Father.Name);
            if (npc.Mother != null && npc.Mother.IsAlive)
                sb.AppendLine("Mother: " + npc.Mother.Name);
            
            if (npc.Spouse != null && npc.Spouse.IsAlive)
            {
                if (npc.Spouse == player)
                    sb.AppendLine("STATUS: Married to Player");
                else
                    sb.AppendLine("Spouse: " + npc.Spouse.Name);
            }

            if (npc.Children != null && npc.Children.Count > 0)
            {
                int aliveChildren = 0;
                foreach (var child in npc.Children)
                {
                    if (child.IsAlive) aliveChildren++;
                }
                if (aliveChildren > 0)
                    sb.AppendLine($"Children: {aliveChildren} living");
            }

            // ================================================================
            // MEDICI ENGINE: Political Intrigue Context
            // ================================================================
            string npcId = GetNpcId(npc);

            sb.AppendLine();
            sb.AppendLine("[POLITICAL CONTEXT]");
            sb.AppendLine(Medici.MediciManager.GetPlayerReputationString());

            if (Medici.MediciState.HeroFactions.TryGetValue(npcId, out var faction) && faction != Medici.PoliticalFaction.Unaligned)
            {
                sb.AppendLine("NPC Faction: " + faction);
            }

            if (Medici.MediciState.FavorsOwedToPlayer.TryGetValue(npcId, out var favors) && favors.Count > 0)
            {
                sb.AppendLine($"This NPC owes the Player {favors.Count} favor(s):");
                foreach (var favor in favors)
                {
                    sb.AppendLine($"- Magnitude: {favor.Magnitude}, Reason: {favor.Reason}");
                }
            }

            if (Medici.MediciState.PlayerLeverage.TryGetValue(npcId, out var leverage) && leverage.Count > 0)
            {
                sb.AppendLine($"Player holds leverage/blackmail over this NPC:");
                foreach (var secret in leverage)
                {
                    sb.AppendLine($"- {secret}");
                }
            }

            // DEEP LAYER: Native Friends and Enemies
            // PERF: Only scan Lords to avoid O(N^2) on 10k+ heroes
            var friends = TaleWorlds.CampaignSystem.Campaign.Current.AliveHeroes
                .Where(h => h != npc && h.IsLord && h.Age >= 18 && h.GetRelation(npc) >= 30)
                .OrderByDescending(h => h.GetRelation(npc))
                .Take(2).ToList();

            var enemies = TaleWorlds.CampaignSystem.Campaign.Current.AliveHeroes
                .Where(h => h != npc && h.IsLord && h.Age >= 18 && h.GetRelation(npc) <= -30)
                .OrderBy(h => h.GetRelation(npc))
                .Take(2).ToList();

            if (friends.Count > 0)
            {
                sb.AppendLine("Closest Friends (will naturally defend them):");
                foreach(var f in friends) sb.AppendLine($"- {f.Name} (Relation: {f.GetRelation(npc)})");
            }
            if (enemies.Count > 0)
            {
                sb.AppendLine("Bitter Enemies (will naturally oppose them):");
                foreach(var e in enemies) sb.AppendLine($"- {e.Name} (Relation: {e.GetRelation(npc)})");
            }

            return sb.ToString();
        }

        // ================================================================
        // INVENTORY CONTEXT (RP Items)
        // ================================================================

        /// <summary>
        /// List RP items in player inventory that the AI should know about.
        /// DESIGN: From AIInfluence v3.3.7 — RP items always visible in prompt.
        /// </summary>
        private static string BuildInventoryContext(Hero player)
        {
            // Inject dynamically generated RP items (letters, quest items, spy reports)
            return Quests.LetterSystem.GetInventoryRPContext();
        }

        // ================================================================
        // HELPER METHODS
        // ================================================================

        /// <summary>
        /// Get a stable unique ID for an NPC.
        /// </summary>
        public static string GetNpcId(Hero hero)
        {
            // DESIGN: Use StringId which is stable across saves
            if (hero.CharacterObject != null)
                return hero.CharacterObject.StringId;
            return hero.StringId;
        }

        private static string GetLocationDescription(Hero npc)
        {
            if (npc.CurrentSettlement != null)
            {
                var settlement = npc.CurrentSettlement;
                string type = settlement.IsTown ? "town" :
                              settlement.IsCastle ? "castle" :
                              settlement.IsVillage ? "village" : "settlement";
                return settlement.Name + " (" + type + ")";
            }

            if (npc.PartyBelongedTo != null)
                return "traveling with party on the campaign map";

            return "unknown location";
        }

        private static string GetSettlementStatus(Settlement settlement, Hero player)
        {
            if (player.Clan == null) return "neutral";

            if (settlement.OwnerClan == player.Clan)
                return "player-owned";

            if (player.Clan.Kingdom != null && settlement.OwnerClan != null &&
                settlement.OwnerClan.Kingdom == player.Clan.Kingdom)
                return "allied";

            if (player.Clan.Kingdom != null && settlement.OwnerClan != null &&
                settlement.OwnerClan.Kingdom != null &&
                player.Clan.Kingdom.IsAtWarWith(settlement.OwnerClan.Kingdom))
                return "enemy territory";

            return "neutral";
        }

        private static string GetFactionRelation(
            TaleWorlds.CampaignSystem.Kingdom npcKingdom,
            TaleWorlds.CampaignSystem.Kingdom playerKingdom)
        {
            if (npcKingdom == playerKingdom)
                return "same kingdom (allies)";
            if (npcKingdom.IsAtWarWith(playerKingdom))
                return "AT WAR";
            
            string dipKey = Medici.MediciState.GetDiplomaticKey(npcKingdom.StringId, playerKingdom.StringId);
            if (Medici.MediciState.Alliances.Contains(dipKey))
                return "ALLIED";
            if (Medici.MediciState.NonAggressionPacts.Contains(dipKey))
                return "Non-Aggression Pact";
                
            return "neutral";
        }

        private static string GetNearbyInfo(Hero npc)
        {
            // TODO: Implement nearby party/settlement detection
            // Will iterate MobileParty.All within a distance threshold
            return null;
        }

        private static string GetActiveWars(Hero npc)
        {
            if (npc.Clan == null || npc.Clan.Kingdom == null) return null;

            var kingdom = npc.Clan.Kingdom;
            var sb = new StringBuilder();
            int count = 0;

            foreach (var enemy in TaleWorlds.CampaignSystem.Campaign.Current.Kingdoms)
            {
                if (enemy == kingdom) continue;
                if (TaleWorlds.CampaignSystem.FactionManager.IsAtWarAgainstFaction(kingdom, enemy) && count < 3)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(enemy.Name);
                    count++;
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static string GetRelationLabel(int relation)
        {
            // DESIGN: Matches our Medici Engine relationship gates
            if (relation >= 80) return "devoted";
            if (relation >= 50) return "trusted ally";
            if (relation >= 20) return "friendly";
            if (relation >= -19) return "neutral";
            if (relation >= -49) return "hostile";
            return "enemy";
        }
    }
}
