using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using LothbrokAI.Core;

namespace LothbrokAI.Testing
{
    /// <summary>
    /// In-Game Developer Console Test Harness.
    /// Due to the nature of the TaleWorlds Campaign engine, head-less unit testing 
    /// is severely restricted. This harness allows tests to be executed via 
    /// the developer console when the game state is fully initialized.
    /// </summary>
    public static class LothbrokTestHarness
    {
        public static string RunAllTests()
        {
            if (Campaign.Current == null)
            {
                return "FAIL: Campaign is not loaded. Tests must be run from within an active save/campaign.";
            }

            int passed = 0;
            int failed = 0;
            var output = new System.Text.StringBuilder();

            Action<string, bool> Assert = (testName, condition) => {
                if (condition)
                {
                    passed++;
                    LothbrokSubModule.Log($"[TEST PASS] {testName}", Debug.DebugColor.Green);
                }
                else
                {
                    failed++;
                    output.AppendLine($"[TEST FAIL] {testName}");
                    LothbrokSubModule.Log($"[TEST FAIL] {testName}", Debug.DebugColor.Red);
                }
            };

            output.AppendLine("--- LOTHBROK AI TEST HARNESS STARTING ---");

            // ==========================================
            // TEST 1: ResponseProcessor Pure Dialogue
            // ==========================================
            try
            {
                var response = ResponseProcessor.Parse("Greetings traveler. I have no actions for you.");
                Assert("ResponseProcessor_PureDialogue", 
                    response.Actions.Count == 0 && response.DialogueText.Contains("Greetings"));
            }
            catch (Exception ex) { Assert($"ResponseProcessor_PureDialogue EXCEPTION: {ex.Message}", false); }

            // ==========================================
            // TEST 2: ResponseProcessor Structured Actions
            // ==========================================
            try
            {
                string raw = "I am granting you favor.\n[ACTIONS]\n{\"actions\": [{\"type\":\"grant_favor\", \"value\": 1}]}";
                var response = ResponseProcessor.Parse(raw);
                Assert("ResponseProcessor_StructuredActions", 
                    response.Actions.Count == 1 && response.Actions[0].Type == "grant_favor" && response.Actions[0].Value == 1);
            }
            catch (Exception ex) { Assert($"ResponseProcessor_StructuredActions EXCEPTION: {ex.Message}", false); }

            // ==========================================
            // TEST 3: ResponseProcessor Broken JSON Fallback
            // ==========================================
            try
            {
                string brokenRaw = "Here is bad json\n[ACTIONS]\n{broken_json_without_quotes: 1}";
                var response = ResponseProcessor.Parse(brokenRaw);
                Assert("ResponseProcessor_BrokenJsonFallback", 
                    response.Actions.Count == 0 && response.DialogueText.Contains("bad json"));
            }
            catch (Exception ex) { Assert($"ResponseProcessor_BrokenJsonFallback EXCEPTION: {ex.Message}", false); }

            // ==========================================
            // TEST 4: ToolRegistry Omni-Tool Generation
            // ==========================================
            try
            {
                var tools = ToolRegistry.GetAvailableTools();
                Assert("ToolRegistry_OmniToolGeneration", tools.Count >= 3);
            }
            catch (Exception ex) { Assert($"ToolRegistry_OmniToolGeneration EXCEPTION: {ex.Message}", false); }

            // ==========================================
            // TEST 5: ToolRegistry Query Execution (Safe Read)
            // ==========================================
            try
            {
                string jsonArgs = "{\"kingdom_name\": \"Vlandia\", \"query_type\": \"strength_and_clans\"}";
                var result = ToolRegistry.ExecuteTool("query_kingdom", jsonArgs);
                Assert("ToolRegistry_QueryKingdomExecution", !result.Contains("Error executing tool"));
            }
            catch (Exception ex) { Assert($"ToolRegistry_QueryKingdomExecution EXCEPTION: {ex.Message}", false); }

            output.AppendLine($"--- TESTS COMPLETE. Passed: {passed}, Failed: {failed} ---");
            return output.ToString();
        }
    }
}
