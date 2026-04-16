using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LothbrokAI.Core
{
    /// <summary>
    /// Parses LLM responses and extracts structured actions.
    /// 
    /// DESIGN: The LLM response can contain:
    /// 1. Plain dialogue text (the NPC's spoken words)
    /// 2. Action markers (things the NPC does: *draws sword*, *hands over letter*)
    /// 3. Structured actions (JSON block for game-mechanical effects)
    /// 
    /// The structured action block is OPTIONAL. If absent, the response
    /// is pure dialogue. If present, it's validated by ActionEngine before
    /// execution. The LLM cannot execute actions directly — it can only
    /// REQUEST them.
    /// 
    /// Format expected from LLM:
    /// ```
    /// [dialogue text here]
    /// 
    /// [ACTIONS]
    /// {"actions": [{"type": "relation_change", "value": 5}, ...]}
    /// ```
    /// </summary>
    public static class ResponseProcessor
    {
        /// <summary>
        /// Parse an LLM response into dialogue text and optional actions.
        /// </summary>
        public static ParsedResponse Parse(string rawResponse)
        {
            if (string.IsNullOrEmpty(rawResponse))
            {
                return new ParsedResponse
                {
                    DialogueText = "[NPC says nothing]",
                    Actions = new List<NpcAction>()
                };
            }

            var result = new ParsedResponse();

            // Look for action block
            int actionMarker = rawResponse.IndexOf("[ACTIONS]", StringComparison.OrdinalIgnoreCase);

            if (actionMarker >= 0)
            {
                // Split dialogue from actions
                result.DialogueText = rawResponse.Substring(0, actionMarker).Trim();

                string actionBlock = rawResponse.Substring(actionMarker + "[ACTIONS]".Length).Trim();
                result.Actions = ParseActions(actionBlock);
            }
            else
            {
                // Pure dialogue, no actions
                result.DialogueText = rawResponse.Trim();
                result.Actions = new List<NpcAction>();
            }

            // Extract emotional tone from dialogue
            result.DetectedTone = DetectTone(result.DialogueText);

            // Clean up common LLM artifacts
            result.DialogueText = CleanDialogue(result.DialogueText);

            return result;
        }

        // ================================================================
        // ACTION PARSING
        // ================================================================

        private static List<NpcAction> ParseActions(string actionBlock)
        {
            var actions = new List<NpcAction>();

            try
            {
                // Try to parse as JSON
                // Handle both {"actions": [...]} and bare [...]
                JToken token = JToken.Parse(actionBlock);

                JArray actionArray;
                if (token is JObject obj && obj["actions"] != null)
                {
                    actionArray = (JArray)obj["actions"];
                }
                else if (token is JArray arr)
                {
                    actionArray = arr;
                }
                else
                {
                    return actions;
                }

                foreach (JObject actionObj in actionArray)
                {
                    var action = new NpcAction
                    {
                        Type = actionObj["type"]?.ToString() ?? "unknown",
                        RawData = actionObj
                    };

                    // Parse common fields
                    if (actionObj["value"] != null)
                        action.Value = actionObj["value"].Value<float>();
                    if (actionObj["target"] != null)
                        action.Target = actionObj["target"].ToString();
                    if (actionObj["settlement_id"] != null)
                        action.SettlementId = actionObj["settlement_id"].ToString();
                    if (actionObj["item_name"] != null)
                        action.ItemName = actionObj["item_name"].ToString();
                    if (actionObj["gold"] != null)
                        action.GoldAmount = actionObj["gold"].Value<int>();

                    actions.Add(action);
                }
            }
            catch (Exception ex)
            {
                // JSON parse failure — log but don't crash
                // DESIGN: Let it crash loud during dev
                LothbrokSubModule.Log("Action parse failed: " + ex.Message,
                    TaleWorlds.Library.Debug.DebugColor.Yellow);
                LothbrokSubModule.Log("Raw action block: " + actionBlock,
                    TaleWorlds.Library.Debug.DebugColor.Yellow);
            }

            return actions;
        }

        // ================================================================
        // TONE DETECTION
        // ================================================================

        /// <summary>
        /// Detect emotional tone from dialogue text.
        /// Used for: emotional state updates, NPC animation, trust changes.
        /// </summary>
        private static string DetectTone(string text)
        {
            if (string.IsNullOrEmpty(text)) return "neutral";

            string lower = text.ToLowerInvariant();

            // Check for strong signals
            // DESIGN: Simple keyword matching. Phase 2 can use LLM classification.
            if (ContainsAny(lower, "furious", "rage", "kill you", "die!", "betray"))
                return "hostile";
            if (ContainsAny(lower, "angry", "upset", "disgust", "how dare"))
                return "angry";
            if (ContainsAny(lower, "love", "darling", "beloved", "my heart", "kiss"))
                return "romantic";
            if (ContainsAny(lower, "thank", "grateful", "friend", "trust", "honor"))
                return "friendly";
            if (ContainsAny(lower, "afraid", "scared", "please don't", "mercy"))
                return "fearful";
            if (ContainsAny(lower, "sad", "mourn", "lost", "grief", "miss"))
                return "melancholy";
            if (ContainsAny(lower, "laugh", "haha", "jest", "joke", "amusing"))
                return "amused";

            return "neutral";
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            foreach (string kw in keywords)
            {
                if (text.IndexOf(kw, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        // ================================================================
        // DIALOGUE CLEANUP
        // ================================================================

        /// <summary>
        /// Clean up common LLM artifacts from dialogue.
        /// </summary>
        private static string CleanDialogue(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Remove internal reasoning that leaked through
            // (CoT template says "Do not output these steps" but LLMs sometimes do)
            text = RemoveSection(text, "[INTERNAL", "]");
            text = RemoveSection(text, "Step 1:", "\n\n");
            text = RemoveSection(text, "FACTS:", "\n\n");

            // Remove double quotes wrapping entire response
            if (text.Length > 2 && text[0] == '"' && text[text.Length - 1] == '"')
                text = text.Substring(1, text.Length - 2);

            // Remove "NPC_NAME:" prefix if LLM included it
            int colonPos = text.IndexOf(':');
            if (colonPos > 0 && colonPos < 30)
            {
                string prefix = text.Substring(0, colonPos).Trim();
                // Only strip if it looks like a name (no spaces, starts with uppercase)
                if (!prefix.Contains(" ") || char.IsUpper(prefix[0]))
                {
                    // Don't strip if it's a real sentence with a colon
                    string afterColon = text.Substring(colonPos + 1).Trim();
                    if (afterColon.Length > 0 && char.IsUpper(afterColon[0]))
                    {
                        text = afterColon;
                    }
                }
            }

            return text.Trim();
        }

        private static string RemoveSection(string text, string startMarker, string endMarker)
        {
            int start = text.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return text;

            int end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = text.Length;
            else end += endMarker.Length;

            return text.Substring(0, start) + text.Substring(end);
        }
    }

    // ================================================================
    // DATA MODELS
    // ================================================================

    /// <summary>
    /// Parsed LLM response — dialogue text + optional game actions.
    /// </summary>
    public class ParsedResponse
    {
        public string DialogueText { get; set; }
        public List<NpcAction> Actions { get; set; }
        public string DetectedTone { get; set; }
    }

    /// <summary>
    /// A single action requested by the NPC.
    /// Must be validated by ActionEngine before execution.
    /// 
    /// DESIGN: Actions are REQUESTS, not commands. The ActionEngine
    /// checks game state (relationship level, skill checks, feasibility)
    /// before allowing execution. "Game decides, AI narrates."
    /// </summary>
    public class NpcAction
    {
        /// <summary>Action type identifier</summary>
        public string Type { get; set; }

        /// <summary>Numeric value (relation change amount, gold, etc.)</summary>
        public float Value { get; set; }

        /// <summary>Target NPC/hero string_id</summary>
        public string Target { get; set; }

        /// <summary>Target settlement string_id</summary>
        public string SettlementId { get; set; }

        /// <summary>Item name for RP item creation</summary>
        public string ItemName { get; set; }

        /// <summary>Gold amount for transfers</summary>
        public int GoldAmount { get; set; }

        /// <summary>Raw JSON for action-specific parsing</summary>
        [JsonIgnore]
        public JObject RawData { get; set; }
    }
}
