using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using System.Collections.Concurrent;

namespace LothbrokAI.Core
{
    /// <summary>
    /// Validates and executes NPC action requests against game state.
    /// 
    /// DESIGN: This is the enforcement layer for "Game decides, AI narrates."
    /// The LLM can REQUEST any action. The ActionEngine checks:
    /// 1. Is this action type valid?
    /// 2. Does the relationship level permit it? (Medici Engine gates)
    /// 3. Does the game state allow it? (skills, items, location)
    /// 4. If valid → execute via Bannerlord API
    /// 5. If invalid → return rejection reason (LLM narrates the failure)
    /// 
    /// Actions are NEVER executed blindly. Every action goes through
    /// validation. The AI can suggest anything — the game has the final word.
    /// </summary>
    public static class ActionEngine
    {
        // ================================================================
        // ACTION REGISTRY
        // ================================================================

        // DESIGN: Each action type maps to a handler with validation + execution.
        // New action types are added by registering handlers here.
        private static readonly Dictionary<string, ActionHandler> _handlers
            = new Dictionary<string, ActionHandler>(StringComparer.OrdinalIgnoreCase)
        {
            { "relation_change", new RelationChangeHandler() },
            { "give_gold", new GiveGoldHandler() },
            { "give_item", new GiveItemHandler() },
            { "create_rp_item", new CreateRPItemHandler() },
            { "start_quest", new StartQuestHandler() },
            { "romance_advance", new RomanceHandler() },
            { "trust_change", new TrustChangeHandler() },
            // Medici Engine (Phase 2):
            { "grant_favor", new GrantFavorHandler() },
            { "use_leverage", new UseLeverageHandler() },
            { "spread_rumor", new SpreadRumorHandler() },
            { "modify_reputation", new ModifyReputationHandler() },
            
            // Faction & Diplomatic Engine (Phase 8.1):
            { "declare_war", new DeclareWarHandler() },
            { "propose_peace", new ProposePeaceHandler() },
            { "propose_alliance", new ProposeAllianceHandler() },
            { "break_alliance", new BreakAllianceHandler() },
            { "transfer_fief", new TransferFiefHandler() }
        };

        // ================================================================
        // PUBLIC THREAD-SAFE QUEUE
        // ================================================================

        public static ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();

        public static void ProcessQueuedActions()
        {
            while (MainThreadQueue.TryDequeue(out Action act))
            {
                act.Invoke();
            }
        }

        // ================================================================
        // PUBLIC API
        // ================================================================

        /// <summary>
        /// Process all actions from a parsed LLM response.
        /// Returns a summary of what happened (for narration).
        /// </summary>
        public static ActionResult ProcessActions(
            List<NpcAction> actions, Hero npc, Hero player)
        {
            var result = new ActionResult();

            foreach (var action in actions)
            {
                if (!_handlers.ContainsKey(action.Type))
                {
                    LothbrokSubModule.Log("Unknown action type: " + action.Type,
                        Debug.DebugColor.Yellow);
                    result.Rejections.Add("Unknown action: " + action.Type);
                    continue;
                }

                var handler = _handlers[action.Type];

                // ── VALIDATION (the gate) ──
                string rejection = handler.Validate(action, npc, player);
                if (rejection != null)
                {
                    LothbrokSubModule.Log("Action REJECTED [" + action.Type + "]: " + rejection);
                    result.Rejections.Add(action.Type + ": " + rejection);
                    continue;
                }

                // ── EXECUTION (game decides) ──
                try
                {
                    handler.Execute(action, npc, player);
                    result.Executed.Add(action.Type);
                    LothbrokSubModule.Log("Action EXECUTED: " + action.Type,
                        Debug.DebugColor.Green);
                }
                catch (Exception ex)
                {
                    LothbrokSubModule.Log("Action FAILED [" + action.Type + "]: " + ex.Message,
                        Debug.DebugColor.Red);
                    result.Rejections.Add(action.Type + " failed: " + ex.Message);
                }
            }

            return result;
        }

        /// <summary>
        /// Get the relationship gate level required for an action type.
        /// Returns the minimum relation needed (-100 to +100).
        /// </summary>
        public static int GetRelationGate(string actionType)
        {
            switch (actionType.ToLowerInvariant())
            {
                // Low barrier — anyone can do these
                case "relation_change": return -100; // Always allowed (game handles magnitude)
                case "trust_change": return -100;    // Trust can change from any interaction

                case "give_gold": return -19;        // Neutral or better
                case "give_item": return -19;
                case "create_rp_item": return -19;
                case "modify_reputation": return -19; // World reaction to player

                // Medium-High barrier
                case "grant_favor": return 10;
                case "use_leverage": return -100;    // Leverage supersedes relation
                case "spread_rumor": return 20;

                // High barrier — need real relationship
                case "start_quest": return 0;        // At least slightly positive
                case "romance_advance": return 20;   // Friendly minimum
                case "transfer_troops": return 30;
                case "transfer_fief": return 50;   // Trusted ally

                // Highest barrier — intimate trust or High Rank
                case "declare_war": return 0; // Gated by Kingdom Ruler status
                case "propose_peace": return 0;
                case "propose_alliance": return 20;
                case "transfer_kingdom": return 70;   // Near-devotion
                case "npc_follow": return 10;
                case "npc_goto": return 10;

                default: return 0;
            }
        }
    }

    // ================================================================
    // ACTION HANDLER BASE
    // ================================================================

    /// <summary>
    /// Base class for action handlers.
    /// Each handler validates against game state and executes via game API.
    /// </summary>
    public abstract class ActionHandler
    {
        /// <summary>
        /// Validate the action. Returns null if valid, rejection reason if not.
        /// </summary>
        public abstract string Validate(NpcAction action, Hero npc, Hero player);

        /// <summary>
        /// Execute the action using Bannerlord's game API.
        /// Only called after Validate returns null.
        /// </summary>
        public abstract void Execute(NpcAction action, Hero npc, Hero player);

        /// <summary>
        /// Check the relationship gate for this action.
        /// </summary>
        protected string CheckRelationGate(Hero npc, Hero player, string actionType)
        {
            int relation = npc.GetRelation(player);
            int gate = ActionEngine.GetRelationGate(actionType);

            if (relation < gate)
            {
                return string.Format(
                    "Relationship too low ({0}) — needs at least {1}",
                    relation, gate);
            }
            return null;
        }
    }

    // ================================================================
    // CONCRETE HANDLERS
    // ================================================================

    /// <summary>
    /// Changes relation between NPC and player.
    /// Value clamped to prevent extreme swings from single conversations.
    /// </summary>
    public class RelationChangeHandler : ActionHandler
    {
        // DESIGN: Max relation change per conversation = ±10
        // Prevents LLM from saying "I love you now" and setting +100
        private const int MAX_CHANGE = 10;

        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            return null; // Always valid — magnitude is clamped
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            int change = (int)Math.Max(-MAX_CHANGE, Math.Min(MAX_CHANGE, action.Value));
            if (change == 0) return;

            ChangeRelationAction.ApplyRelationChangeBetweenHeroes(player, npc, change);
            LothbrokSubModule.Log(string.Format(
                "Relation {0} → {1}: {2}{3}",
                npc.Name, player.Name, change > 0 ? "+" : "", change));
        }
    }

    /// <summary>
    /// NPC gives gold to player (or vice versa).
    /// Validated against actual gold holdings.
    /// </summary>
    public class GiveGoldHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            var gateCheck = CheckRelationGate(npc, player, "give_gold");
            if (gateCheck != null) return gateCheck;

            int amount = action.GoldAmount;
            if (amount <= 0) return "Gold amount must be positive";

            // Check who's giving
            if (amount > 0 && npc.Gold < amount)
                return "NPC doesn't have enough gold (has " + npc.Gold + ")";

            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            int amount = action.GoldAmount;
            GiveGoldAction.ApplyBetweenCharacters(npc, player, amount);
            LothbrokSubModule.Log(string.Format(
                "{0} gave {1} gold to {2}", npc.Name, amount, player.Name));
        }
    }

    /// <summary>
    /// NPC gives item to player.
    /// Validated against NPC's actual inventory.
    /// </summary>
    public class GiveItemHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            var gateCheck = CheckRelationGate(npc, player, "give_item");
            if (gateCheck != null) return gateCheck;

            // TODO: Validate item exists in NPC inventory
            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            // TODO: Transfer item from NPC to player inventory
            LothbrokSubModule.Log("Item transfer: " + action.ItemName +
                " (TODO: implement item transfer)");
        }
    }

    /// <summary>
    /// Creates a new RP item (letter, document, evidence).
    /// Generated by LLM with name and description.
    /// </summary>
    public class CreateRPItemHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            var gateCheck = CheckRelationGate(npc, player, "create_rp_item");
            if (gateCheck != null) return gateCheck;

            if (string.IsNullOrEmpty(action.ItemName))
                return "RP item needs a name";
            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            // Connect to Phase 4: LetterSystem
            Quests.LetterSystem.CreateRPItem(action.ItemName, "Content parsed from conversation context");
        }
    }

    /// <summary>
    /// Starts a quest from NPC dialogue.
    /// Validated by QuestValidator in Phase 4.
    /// </summary>
    public class StartQuestHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            var gateCheck = CheckRelationGate(npc, player, "start_quest");
            if (gateCheck != null) return gateCheck;

            if (string.IsNullOrEmpty(action.ItemName)) return "Quest requires description text";
            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            // Connect to Phase 4: QuestEngine
            int magnitude = action.Value > 0 ? (int)action.Value : 1;
            Quests.QuestEngine.GenerateQuest(npc, player, action.ItemName, magnitude);
        }
    }

    /// <summary>
    /// Advances romance with NPC.
    /// Gated by relationship level AND game-state checks.
    /// </summary>
    public class RomanceHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            var gateCheck = CheckRelationGate(npc, player, "romance_advance");
            if (gateCheck != null) return gateCheck;

            // Can't romance your own gender (vanilla restriction)
            // Unless Norse polygamy is active (Phase 2)
            if (npc.IsFemale == player.IsFemale)
                return "Same-gender romance not supported in vanilla";

            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            // TODO: Phase 2 — RomanceSystem handles this
            LothbrokSubModule.Log("Romance advanced with " + npc.Name +
                " (TODO: implement RomanceSystem)");
        }
    }

    /// <summary>
    /// Changes trust level (our custom metric, not game relation).
    /// </summary>
    public class TrustChangeHandler : ActionHandler
    {
        private const float MAX_TRUST_CHANGE = 0.1f; // Max ±10% per conversation

        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            return null; // Always valid — magnitude clamped
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            float change = Math.Max(-MAX_TRUST_CHANGE,
                Math.Min(MAX_TRUST_CHANGE, action.Value / 100f));

            string npcId = ContextAssembler.GetNpcId(npc);
            var payload = Memory.MemoryEngine.Retrieve(npcId, npc.Name.ToString(), "");
            float newTrust = Math.Max(0f, Math.Min(1f,
                payload.Metadata.TrustLevel + change));

            // TODO: Update trust in metadata
            LothbrokSubModule.Log(string.Format(
                "Trust {0}: {1:F2} → {2:F2}",
                npc.Name, payload.Metadata.TrustLevel, newTrust));
        }
    }

    // ================================================================
    // MEDICI ENGINE HANDLERS (PHASE 2)
    // ================================================================

    public class GrantFavorHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            var gateCheck = CheckRelationGate(npc, player, "grant_favor");
            if (gateCheck != null) return gateCheck;

            if (action.Value <= 0 || action.Value > 10) return "Invalid favor magnitude (must be 1-10)";
            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            // For now, magnitude mapping: 1=Minor, 3=Major, 5=LifeDebt
            Medici.FavorMagnitude mag = Medici.FavorMagnitude.Minor;
            if (action.Value >= 5) mag = Medici.FavorMagnitude.LifeDebt;
            else if (action.Value >= 3) mag = Medici.FavorMagnitude.Major;
            else if (action.Value >= 2) mag = Medici.FavorMagnitude.Moderate;

            string reason = string.IsNullOrEmpty(action.ItemName) ? "Unspecified favor" : action.ItemName;
            Medici.MediciManager.GrantFavor(npc, player, mag, reason);
        }
    }

    public class UseLeverageHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            // Gate check is bypassed because leverage forces compliance regardless of hatred
            string npcId = ContextAssembler.GetNpcId(npc);
            if (!Medici.MediciState.PlayerLeverage.ContainsKey(npcId) || Medici.MediciState.PlayerLeverage[npcId].Count == 0)
                return "The Player currently holds no active leverage over this NPC.";
            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            LothbrokSubModule.Log($"Player executed leverage against {npc.Name}.", TaleWorlds.Library.Debug.DebugColor.Green);
            // In a deeper implementation, this would remove the leverage and force a companion action.
        }
    }

    public class SpreadRumorHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            var gateCheck = CheckRelationGate(npc, player, "spread_rumor");
            if (gateCheck != null) return gateCheck;

            if (string.IsNullOrEmpty(action.ItemName)) return "Rumor content cannot be empty.";

            // We do NOT hard-code "Betrayal Protection" here. 
            // The AI knows its Friends/Enemies via ContextAssembler. 
            // We let the generative AI decide if its loyalty, traits, or the player's 
            // relationship override its friendship ties.

            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            string rumorId = Guid.NewGuid().ToString();
            Medici.MediciState.ActiveRumors[rumorId] = new Medici.RumorData()
            {
                RumorId = rumorId,
                Content = action.ItemName,
                TargetId = ContextAssembler.GetNpcId(npc),
                Severity = action.Value > 0 ? (int)action.Value : 3,
                CreatedDay = (float)CampaignTime.Now.ToDays
            };
            LothbrokSubModule.Log($"Rumor started by {npc.Name} circulating: '{action.ItemName}'", TaleWorlds.Library.Debug.DebugColor.Green);
        }
    }

    public class ModifyReputationHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            return null; // Global world reaction
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            float honorDelta = 0;
            float fearDelta = 0;
            float influenceDelta = 0;

            // In ResponseProcessor, the ItemName field will hold the axis (Honor, Fear, Influence)
            string axis = string.IsNullOrEmpty(action.ItemName) ? "" : action.ItemName.ToLowerInvariant();
            if (axis == "honor") honorDelta = action.Value;
            else if (axis == "fear") fearDelta = action.Value;
            else if (axis == "influence") influenceDelta = action.Value;

            Medici.MediciManager.ModifyReputation(honorDelta, fearDelta, influenceDelta);
            LothbrokSubModule.Log($"Player Reputation Shift: {axis} {(action.Value >= 0 ? "+" : "")}{action.Value}", TaleWorlds.Library.Debug.DebugColor.Blue);
        }
    }

    // ================================================================
    // DIPLOMATIC HANDLERS (PHASE 8.1)
    // ================================================================

    public class DeclareWarHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            if (npc.Clan?.Kingdom == null) return "NPC is not part of a Kingdom.";
            if (npc.Clan.Kingdom.Leader != npc) return "Only the faction ruler can declare war directly.";
            if (string.IsNullOrEmpty(action.Target)) return "Target Kingdom name required.";
            
            var targetKingdom = Campaign.Current.Kingdoms.FirstOrDefault(k => k.Name.ToString().Equals(action.Target, StringComparison.OrdinalIgnoreCase));
            if (targetKingdom == null) return $"Kingdom '{action.Target}' not found.";
            if (npc.Clan.Kingdom.IsAtWarWith(targetKingdom)) return $"Already at war with {targetKingdom.Name}.";

            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            var targetKingdom = Campaign.Current.Kingdoms.FirstOrDefault(k => k.Name.ToString().Equals(action.Target, StringComparison.OrdinalIgnoreCase));
            DeclareWarAction.ApplyByDefault(npc.Clan.Kingdom, targetKingdom);
            LothbrokSubModule.Log($"{npc.Name} declared WAR on {targetKingdom.Name}!", TaleWorlds.Library.Debug.DebugColor.Red);
        }
    }

    public class ProposePeaceHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            if (npc.Clan?.Kingdom == null) return "NPC is not part of a Kingdom.";
            if (npc.Clan.Kingdom.Leader != npc) return "Only the faction ruler can negotiate peace directly.";
            if (string.IsNullOrEmpty(action.Target)) return "Target Kingdom name required.";
            
            var targetKingdom = Campaign.Current.Kingdoms.FirstOrDefault(k => k.Name.ToString().Equals(action.Target, StringComparison.OrdinalIgnoreCase));
            if (targetKingdom == null) return $"Kingdom '{action.Target}' not found.";
            if (!npc.Clan.Kingdom.IsAtWarWith(targetKingdom)) return $"Not at war with {targetKingdom.Name}.";

            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            var targetKingdom = Campaign.Current.Kingdoms.FirstOrDefault(k => k.Name.ToString().Equals(action.Target, StringComparison.OrdinalIgnoreCase));
            MakePeaceAction.Apply(npc.Clan.Kingdom, targetKingdom);
            LothbrokSubModule.Log($"{npc.Name} made PEACE with {targetKingdom.Name}.", TaleWorlds.Library.Debug.DebugColor.Green);
        }
    }

    public class ProposeAllianceHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            if (npc.Clan?.Kingdom == null || player.Clan?.Kingdom == null) return "Both parties must be part of a Kingdom.";
            if (npc.Clan.Kingdom.Leader != npc) return "Only the faction ruler can negotiate alliances.";
            if (npc.Clan.Kingdom == player.Clan.Kingdom) return "Already in the same kingdom.";
            if (npc.Clan.Kingdom.IsAtWarWith(player.Clan.Kingdom)) return "Cannot ally while at war.";

            var gateCheck = CheckRelationGate(npc, player, "propose_alliance");
            if (gateCheck != null) return gateCheck;

            string dipKey = Medici.MediciState.GetDiplomaticKey(npc.Clan.Kingdom.StringId, player.Clan.Kingdom.StringId);
            if (Medici.MediciState.Alliances.Contains(dipKey)) return "Kingdoms are already allied.";

            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            string dipKey = Medici.MediciState.GetDiplomaticKey(npc.Clan.Kingdom.StringId, player.Clan.Kingdom.StringId);
            Medici.MediciState.Alliances.Add(dipKey);
            LothbrokSubModule.Log($"{npc.Name} sealed an ALLIANCE with {player.Clan.Kingdom.Name}.", TaleWorlds.Library.Debug.DebugColor.Green);
        }
    }

    public class BreakAllianceHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            if (npc.Clan?.Kingdom == null || player.Clan?.Kingdom == null) return "Both parties must be part of a Kingdom.";
            if (npc.Clan.Kingdom.Leader != npc) return "Only the faction ruler can break alliances.";

            string dipKey = Medici.MediciState.GetDiplomaticKey(npc.Clan.Kingdom.StringId, player.Clan.Kingdom.StringId);
            if (!Medici.MediciState.Alliances.Contains(dipKey)) return "Kingdoms are not allied.";

            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            string dipKey = Medici.MediciState.GetDiplomaticKey(npc.Clan.Kingdom.StringId, player.Clan.Kingdom.StringId);
            Medici.MediciState.Alliances.Remove(dipKey);
            LothbrokSubModule.Log($"{npc.Name} BROKE the alliance with {player.Clan.Kingdom.Name}.", TaleWorlds.Library.Debug.DebugColor.Red);
        }
    }

    public class TransferFiefHandler : ActionHandler
    {
        public override string Validate(NpcAction action, Hero npc, Hero player)
        {
            var gateCheck = CheckRelationGate(npc, player, "transfer_fief");
            if (gateCheck != null) return gateCheck;
            if (string.IsNullOrEmpty(action.SettlementId)) return "Settlement name required.";
            
            var settlement = Settlement.All.FirstOrDefault(s => s.Name.ToString().Equals(action.SettlementId, StringComparison.OrdinalIgnoreCase));
            if (settlement == null) return $"Settlement '{action.SettlementId}' not found.";
            if (settlement.OwnerClan != npc.Clan) return $"NPC's clan does not own {settlement.Name}.";

            return null;
        }

        public override void Execute(NpcAction action, Hero npc, Hero player)
        {
            var settlement = Settlement.All.FirstOrDefault(s => s.Name.ToString().Equals(action.SettlementId, StringComparison.OrdinalIgnoreCase));
            ChangeOwnerOfSettlementAction.ApplyByGift(settlement, player);
            LothbrokSubModule.Log($"{npc.Name} gifted settlement {settlement.Name} to Player.", TaleWorlds.Library.Debug.DebugColor.Green);
        }
    }

    // ================================================================
    // RESULT
    // ================================================================

    /// <summary>
    /// Result of action processing — what was executed, what was rejected.
    /// </summary>
    public class ActionResult
    {
        public List<string> Executed { get; set; } = new List<string>();
        public List<string> Rejections { get; set; } = new List<string>();

        public bool HasRejections { get { return Rejections.Count > 0; } }
        public bool HasExecuted { get { return Executed.Count > 0; } }
    }
}
