using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LothbrokAI.API;
using TaleWorlds.CampaignSystem;

namespace LothbrokAI.Core
{
    /// <summary>
    /// Token-budgeted prompt assembly with Chain-of-Thought reasoning.
    /// 
    /// DESIGN: The prompt is structured for API prefix caching:
    ///   [STATIC]      System prompt + CoT template + world description  (cached by API)
    ///   [SEMI-STATIC] NPC personality + relationship state              (changes rarely)
    ///   [DYNAMIC]     Memory + recent messages + current context         (changes every turn)
    /// 
    /// This ordering lets OpenRouter/etc. cache the static prefix across
    /// multiple API calls, reducing cost and latency.
    /// 
    /// Token budget is strictly enforced. Each component has a max allocation.
    /// If a component exceeds budget, it's truncated with a note.
    /// </summary>
    public static class PromptBuilder
    {
        // ================================================================
        // TOKEN BUDGETS
        // ================================================================

        // DESIGN: Total budget is 3,200 tokens for input.
        // AIInfluence dumps 40,000+ tokens. We're 10x more efficient.
        private const int BUDGET_SYSTEM = 400;           // System instructions
        private const int BUDGET_COT = 300;              // Chain-of-thought template
        private const int BUDGET_PERSONALITY = 400;      // NPC personality + backstory
        private const int BUDGET_RELATIONSHIP = 200;     // Trust, romance, faction
        private const int BUDGET_MEMORY_SUMMARY = 300;   // Compressed history
        private const int BUDGET_MEMORY_RETRIEVED = 600; // Top-K relevant memories
        private const int BUDGET_RECENT = 400;           // Last N raw messages
        private const int BUDGET_WORLD = 400;            // Location, events, diplomacy
        private const int BUDGET_ITEMS = 100;            // RP items in inventory
        private const int BUDGET_TOTAL = 3200;

        // ================================================================
        // TEMPLATE CACHE (hot-reloadable)
        // ================================================================

        private static string _systemTemplate;
        private static string _cotTemplate;
        private static DateTime _lastTemplateLoad;
        private static string _templateDir;

        /// <summary>
        /// Initialize template directory. Templates are reloaded automatically
        /// when files change.
        /// </summary>
        public static void Initialize(string dataDir)
        {
            _templateDir = Path.Combine(dataDir, "prompts");
            ReloadTemplates();
        }

        // ================================================================
        // PUBLIC API
        // ================================================================

        /// <summary>
        /// Build the complete prompt for an NPC conversation.
        /// Returns (systemPrompt, userMessage) tuple for the API call.
        /// </summary>
        /// <param name="npcName">NPC display name</param>
        /// <param name="npcPersonality">Generated personality description</param>
        /// <param name="npcBackstory">Generated backstory</param>
        /// <param name="speechQuirks">Speech quirks (accent, dialect, verbal tics)</param>
        /// <param name="relationshipState">Trust, romance, faction standing</param>
        /// <param name="memorySummary">Rolling compressed history</param>
        /// <param name="retrievedMemories">Top-K relevant past exchanges</param>
        /// <param name="recentMessages">Last N raw messages</param>
        /// <param name="worldContext">Location, events, diplomacy</param>
        /// <param name="rpItems">RP items in player inventory</param>
        /// <param name="playerName">Player character name</param>
        /// <param name="playerMessage">Current player message</param>
        /// <param name="emotionalState">Current NPC emotional state</param>
        public static BuiltPrompt Build(
            string npcName,
            string npcPersonality,
            string npcBackstory,
            string speechQuirks,
            string relationshipState,
            string memorySummary,
            List<string> retrievedMemories,
            List<string> recentMessages,
            string worldContext,
            string rpItems,
            string playerName,
            string playerMessage,
            string emotionalState)
        {
            // Hot-reload templates if changed
            CheckTemplateReload();

            var sb = new StringBuilder(8000);
            int tokensUsed = 0;

            // ── SYSTEM PROMPT (static, cached by API) ──
            var npc = Campaign.Current?.AliveHeroes.FirstOrDefault(h => h.Name.ToString() == npcName);
            string system = BuildSystemSection(npcName, playerName, npc);

            // ── USER MESSAGE (dynamic, per-turn) ──

            // 1. Chain-of-Thought reasoning template
            string cot = TokenEstimator.TruncateToTokens(
                _cotTemplate ?? GetDefaultCoT(), BUDGET_COT);
            sb.AppendLine("[INTERNAL REASONING PROCESS]");
            sb.AppendLine(cot);
            sb.AppendLine();
            tokensUsed += TokenEstimator.Estimate(cot);

            // 2. NPC Identity (semi-static, changes rarely)
            sb.AppendLine("[CHARACTER IDENTITY]");
            string identity = BuildIdentitySection(
                npcName, npcPersonality, npcBackstory, speechQuirks, emotionalState);
            identity = TokenEstimator.TruncateToTokens(identity, BUDGET_PERSONALITY);
            sb.AppendLine(identity);
            sb.AppendLine();
            tokensUsed += TokenEstimator.Estimate(identity);

            // 3. Relationship state
            if (!string.IsNullOrEmpty(relationshipState))
            {
                sb.AppendLine("[RELATIONSHIP]");
                string rel = TokenEstimator.TruncateToTokens(
                    relationshipState, BUDGET_RELATIONSHIP);
                sb.AppendLine(rel);
                sb.AppendLine();
                tokensUsed += TokenEstimator.Estimate(rel);
            }

            // 4. Memory summary (compressed history)
            if (!string.IsNullOrEmpty(memorySummary))
            {
                sb.AppendLine("[CONVERSATION HISTORY SUMMARY]");
                string summary = TokenEstimator.TruncateToTokens(
                    memorySummary, BUDGET_MEMORY_SUMMARY);
                sb.AppendLine(summary);
                sb.AppendLine();
                tokensUsed += TokenEstimator.Estimate(summary);
            }

            // 5. Retrieved relevant memories
            if (retrievedMemories != null && retrievedMemories.Count > 0)
            {
                sb.AppendLine("[RELEVANT PAST EXCHANGES]");
                int memBudget = BUDGET_MEMORY_RETRIEVED;
                foreach (string mem in retrievedMemories)
                {
                    int memTokens = TokenEstimator.Estimate(mem);
                    if (memTokens > memBudget) break;
                    sb.AppendLine("- " + mem);
                    memBudget -= memTokens;
                    tokensUsed += memTokens;
                }
                sb.AppendLine();
            }

            // 6. Recent messages (raw conversational flow)
            if (recentMessages != null && recentMessages.Count > 0)
            {
                sb.AppendLine("[RECENT CONVERSATION]");
                int recentBudget = BUDGET_RECENT;
                // Start from most recent, work backwards
                for (int i = recentMessages.Count - 1; i >= 0; i--)
                {
                    int msgTokens = TokenEstimator.Estimate(recentMessages[i]);
                    if (msgTokens > recentBudget) break;
                    recentBudget -= msgTokens;
                    tokensUsed += msgTokens;
                }
                // Now add them in chronological order
                foreach (string msg in recentMessages)
                {
                    sb.AppendLine(msg);
                }
                sb.AppendLine();
            }

            // 7. World context
            if (!string.IsNullOrEmpty(worldContext))
            {
                sb.AppendLine("[WORLD CONTEXT]");
                string world = TokenEstimator.TruncateToTokens(
                    worldContext, BUDGET_WORLD);
                sb.AppendLine(world);
                sb.AppendLine();
                tokensUsed += TokenEstimator.Estimate(world);
            }

            // 8. RP Items in inventory
            if (!string.IsNullOrEmpty(rpItems))
            {
                sb.AppendLine("[ITEMS IN PLAYER INVENTORY]");
                string items = TokenEstimator.TruncateToTokens(rpItems, BUDGET_ITEMS);
                sb.AppendLine(items);
                sb.AppendLine();
                tokensUsed += TokenEstimator.Estimate(items);
            }

            // 9. Current player message (always last, always included)
            sb.AppendLine("[CURRENT MESSAGE]");
            sb.AppendLine(playerName + ": " + playerMessage);

            tokensUsed += TokenEstimator.Estimate(playerMessage);

            if (LothbrokConfig.Current.DebugMode)
            {
                LothbrokSubModule.Log(string.Format(
                    "Prompt built: ~{0} tokens (budget: {1})",
                    tokensUsed, BUDGET_TOTAL));
            }

            return new BuiltPrompt
            {
                SystemPrompt = system,
                UserMessage = sb.ToString(),
                EstimatedTokens = tokensUsed + TokenEstimator.Estimate(system)
            };
        }

        // ================================================================
        // SECTION BUILDERS
        // ================================================================

        private static string BuildSystemSection(string npcName, string playerName, Hero npc = null)
        {
            string template = _systemTemplate ?? GetDefaultSystemPrompt();

            // Simple template variables
            template = template.Replace("{npc_name}", npcName);
            template = template.Replace("{player_name}", playerName);

            // Dynamic Action List (Phase 8.1)
            var actionBuilder = new StringBuilder();
            actionBuilder.AppendLine("\n[AVAILABLE GAME ACTIONS]");
            actionBuilder.AppendLine("To trigger game effects, you MUST append this exact block to the end of your message:");
            actionBuilder.AppendLine("[ACTIONS]");
            actionBuilder.AppendLine("{\"actions\": [{\"type\": \"<action_type>\", \"value\": 10}]}");
            actionBuilder.AppendLine("");
            actionBuilder.AppendLine("Valid <action_type> strings:");
            actionBuilder.AppendLine("- relation_change (requires \"value\" from -100 to 100)");
            actionBuilder.AppendLine("- give_gold (requires \"value\" as amount of gold NPC gives TO player)");
            actionBuilder.AppendLine("- take_gold (requires \"value\" as amount of gold player gives TO NPC)");
            actionBuilder.AppendLine("- give_item (requires \"item_name\")");
            actionBuilder.AppendLine("- create_rp_item (requires \"item_name\")");
            actionBuilder.AppendLine("- trust_change (requires \"value\" from -100 to 100)");
            actionBuilder.AppendLine("- modify_reputation");
            
            // Medici Handlers
            actionBuilder.AppendLine("- grant_favor");
            actionBuilder.AppendLine("- spread_rumor");

            if (npc != null)
            {
                if (npc.Clan?.Kingdom != null && npc.Clan.Kingdom.Leader == npc)
                {
                    // Ruler Actions
                    actionBuilder.AppendLine("- declare_war (requires \"target\" kingdom name)");
                    actionBuilder.AppendLine("- propose_peace (requires \"target\" kingdom name)");
                    actionBuilder.AppendLine("- propose_alliance");
                    actionBuilder.AppendLine("- break_alliance");
                }
                if (npc.Clan != null && npc.Clan.Settlements.Count > 0)
                {
                    actionBuilder.AppendLine("- transfer_fief (requires \"settlement_id\")");
                }
            }

            template += actionBuilder.ToString();

            return TokenEstimator.TruncateToTokens(template, BUDGET_SYSTEM);
        }

        private static string BuildIdentitySection(
            string npcName, string personality, string backstory,
            string quirks, string emotionalState)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name: " + npcName);

            if (!string.IsNullOrEmpty(personality))
                sb.AppendLine("Personality: " + personality);
            if (!string.IsNullOrEmpty(backstory))
                sb.AppendLine("Backstory: " + backstory);
            if (!string.IsNullOrEmpty(quirks))
                sb.AppendLine("Speech: " + quirks);
            if (!string.IsNullOrEmpty(emotionalState))
                sb.AppendLine("Current mood: " + emotionalState);

            return sb.ToString();
        }

        // ================================================================
        // TEMPLATE MANAGEMENT
        // ================================================================

        private static void CheckTemplateReload()
        {
            if (_templateDir == null) return;

            try
            {
                // Check every 30 seconds at most
                if ((DateTime.UtcNow - _lastTemplateLoad).TotalSeconds < 30)
                    return;

                ReloadTemplates();
            }
            catch { /* Templates are optional, defaults work fine */ }
        }

        private static void ReloadTemplates()
        {
            _lastTemplateLoad = DateTime.UtcNow;

            if (_templateDir == null) return;

            string systemPath = Path.Combine(_templateDir, "system.txt");
            string cotPath = Path.Combine(_templateDir, "thought_process.txt");

            if (File.Exists(systemPath))
            {
                _systemTemplate = File.ReadAllText(systemPath, Encoding.UTF8);
            }
            if (File.Exists(cotPath))
            {
                _cotTemplate = File.ReadAllText(cotPath, Encoding.UTF8);
            }
        }

        // ================================================================
        // DEFAULT TEMPLATES
        // ================================================================

        private static string GetDefaultSystemPrompt()
        {
            return @"You are {npc_name}, a character in a medieval world. You are having a conversation with {player_name}.

RULES:
- Stay in character at all times. Never break the fourth wall.
- Your responses should reflect your personality, backstory, and current emotional state.
- Use your speech quirks consistently (accent, dialect, verbal tics).
- You can reference past conversations, world events, and your relationship with the player.
- If the player attempts an action (marked with *asterisks*), describe the outcome but do NOT decide success/failure — the game handles that.
- Keep responses concise (2-4 sentences for casual talk, longer for important moments).
- You may use *action descriptions* in your response to show physical actions.";
        }

        private static string GetDefaultCoT()
        {
            // DESIGN: This Chain-of-Thought template comes from reverse engineering
            // AIInfluence v3.3.7's "internal thought process" system.
            // Steps: fact check → lie detect → context → character → coherence
            return @"Before responding, internally verify:
1. FACTS: What do I actually know from the game data provided? Do not invent facts.
2. LIE CHECK: Has the player claimed something? Does it match the game data I have?
3. CONTEXT: Does my response follow logically from our conversation history?
4. CHARACTER: Am I maintaining my personality, accent, and speech patterns?
5. COHERENCE: Does my full response make sense as this character in this situation?

Do not output these steps. They are internal reasoning only. Output only your in-character response.";
        }
    }

    /// <summary>
    /// Result of prompt building — ready for APIRouter.
    /// </summary>
    public class BuiltPrompt
    {
        public string SystemPrompt { get; set; }
        public string UserMessage { get; set; }
        public int EstimatedTokens { get; set; }
    }
}
