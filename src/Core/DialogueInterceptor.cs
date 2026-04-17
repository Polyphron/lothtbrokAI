using System;
using System.Threading.Tasks;
using LothbrokAI.API;
using LothbrokAI.Core;
using LothbrokAI.Memory;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace LothbrokAI.Core
{
    /// <summary>
    /// Connects the game's dialogue system to the LothbrokAI pipeline.
    /// 
    /// DESIGN: Dialogue stays OPEN while the API runs on a background thread.
    /// The player clicks "[Check for response...]" to poll. When the API finishes,
    /// the NPC's reply node becomes active and displays the AI-generated text.
    /// This mirrors the original AIInfluence mod's conversation flow.
    /// 
    /// FLOW:
    /// hero_main_options → lothbrok_chat_input ("I am listening...")
    ///   → lothbrok_chat_options ([Type a response])
    ///     → lothbrok_chat_waiting ("..." / NPC reply)
    ///       → lothbrok_chat_wait_options ([Check] / [Cancel])
    ///         OR lothbrok_chat_loop_options ([Reply] / [Leave])
    /// </summary>
    public class DialogueInterceptor : CampaignBehaviorBase
    {
        public static string PlayerInputText = "";

        // Async state
        private volatile bool _isAIGenerating = false;
        private string _lastGeneratedResponse = "";
        private string _lastGeneratedActionText = "";

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // SyncData for the mod is mostly handled by SaveManager
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            AddLothbrokDialogues(starter);
        }

        private void AddLothbrokDialogues(CampaignGameStarter starter)
        {
            // ================================================================
            // ENTRY POINT
            // ================================================================

            // Player option to start AI chat
            starter.AddPlayerLine(
                "lothbrok_chat_start",
                "hero_main_options",
                "lothbrok_chat_input",
                "{=lothbrok_talk_opt}I wish to speak with you on a matter...",
                conditionDelegate: () => Hero.OneToOneConversationHero != null,
                consequenceDelegate: () => {
                    PersonalityGenerator.EnsurePersonality(
                        ContextAssembler.GetNpcId(Hero.OneToOneConversationHero),
                        Hero.OneToOneConversationHero);
                });

            // DEBUG: Run tests
            starter.AddPlayerLine(
                "lothbrok_chat_debug_tests",
                "hero_main_options",
                "hero_main_options",
                "[DEBUG] Run AI Test Harness",
                conditionDelegate: () => Hero.OneToOneConversationHero != null,
                consequenceDelegate: () => {
                    string result = LothbrokAI.Testing.LothbrokTestHarness.RunAllTests();
                    InformationManager.ShowInquiry(new InquiryData(
                        "Test Results", result, true, false, "Close", "", null, null));
                });

            // DEBUG: Map Calradia Graph (background thread — no freeze)
            starter.AddPlayerLine(
                "lothbrok_chat_debug_graph",
                "hero_main_options",
                "hero_main_options",
                "[DEBUG] Map Calradia Graph (Graph DB Export)",
                conditionDelegate: () => Hero.OneToOneConversationHero != null,
                consequenceDelegate: () => {
                    InformationManager.DisplayMessage(new InformationMessage(
                        "[LothbrokAI] Exporting graph in background...",
                        new Color(0.5f, 0.8f, 1f)));

                    Task.Run(() => {
                        try
                        {
                            string logDir = System.IO.Path.Combine(LothbrokSubModule.ModDir, "logs");
                            if (!System.IO.Directory.Exists(logDir))
                                System.IO.Directory.CreateDirectory(logDir);
                            string path = System.IO.Path.Combine(logDir, "calradia_graph.json");
                            string result = CalradiaGraphExporter.ExportGraph(path);
                            InformationManager.DisplayMessage(new InformationMessage(
                                "[LothbrokAI] " + result, new Color(0.4f, 1f, 0.4f)));
                        }
                        catch (Exception ex)
                        {
                            InformationManager.DisplayMessage(new InformationMessage(
                                "[LothbrokAI] Graph export failed: " + ex.Message,
                                new Color(1f, 0.3f, 0.3f)));
                        }
                    });
                });

            // ================================================================
            // STATE 1: IDLE / NPC HAS SPOKEN
            // ================================================================

            starter.AddDialogLine(
                "lothbrok_chat_input",
                "lothbrok_chat_input",
                "lothbrok_chat_options",
                "{=lothbrok_listening}I am listening...",
                conditionDelegate: () => {
                    MBTextManager.SetTextVariable("GENERATED_NPC_TEXT", "I am listening...");
                    return true;
                },
                consequenceDelegate: null);

            starter.AddDialogLine(
                "lothbrok_chat_npc_reply",
                "lothbrok_chat_reply_target",
                "lothbrok_chat_options",
                "{GENERATED_NPC_TEXT}",
                conditionDelegate: () => {
                    MBTextManager.SetTextVariable("GENERATED_NPC_TEXT", _lastGeneratedResponse);
                    return true;
                },
                consequenceDelegate: () => {
                    // Show action results as purple banner if any
                    if (!string.IsNullOrEmpty(_lastGeneratedActionText))
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            _lastGeneratedActionText, new Color(0.8f, 0.4f, 1f)));
                        _lastGeneratedActionText = "";
                    }
                });

            // ================================================================
            // OPTIONS FOR STATE 1
            // ================================================================

            starter.AddPlayerLine(
                "lothbrok_chat_type",
                "lothbrok_chat_options",
                "lothbrok_chat_waiting_state", // Jumps here while you type
                "{=lothbrok_type_opt}[Type a response]",
                conditionDelegate: () => true,
                consequenceDelegate: () => {
                    _isAIGenerating = false; // Important: DO NOT set to true yet!
                    var npc = Hero.OneToOneConversationHero;
                    var player = Hero.MainHero;

                    // Show text input popup
                    InformationManager.ShowTextInquiry(new TextInquiryData(
                        "Speak to " + npc.Name,
                        "What do you wish to say?",
                        true, true,
                        "Speak", "Cancel",
                        text => {
                            PlayerInputText = text;
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                // Fire API call on background thread
                                _isAIGenerating = true;
                                Task.Run(() => ProcessConversation(npc, player, text));
                            }
                        },
                        null
                    ));
                });

            // FIX: Return to vanilla dialogue (jump to lord_pretalk instead of looping)
            starter.AddPlayerLine(
                "lothbrok_chat_return",
                "lothbrok_chat_options",
                "lord_pretalk",
                "{=lothbrok_return}[Return to normal matters]",
                conditionDelegate: () => Hero.OneToOneConversationHero != null && Hero.OneToOneConversationHero.IsLord,
                consequenceDelegate: null);

            // Universal exit
            starter.AddPlayerLine(
                "lothbrok_chat_leave",
                "lothbrok_chat_options",
                "close_window",
                "{=lothbrok_farewell}[Leave]",
                conditionDelegate: () => true,
                consequenceDelegate: null);

            // ================================================================
            // STATE 2: WAITING FOR AI
            // ================================================================

            // When user clicks [Type a response], background jumps here.
            // Since it displays {GENERATED_NPC_TEXT}, it shows the OLD reply while they type.
            starter.AddDialogLine(
                "lothbrok_chat_waiting_state",
                "lothbrok_chat_waiting_state",
                "lothbrok_chat_wait_options",
                "{GENERATED_NPC_TEXT}",  
                conditionDelegate: () => true,
                consequenceDelegate: null);

            // ================================================================
            // OPTIONS FOR STATE 2 (WAITING)
            // ================================================================

            // Universal polling option. 
            // They see this immediately after closing the popup.
            starter.AddPlayerLine(
                "lothbrok_chat_check_any",
                "lothbrok_chat_wait_options",
                "lothbrok_chat_evaluate_wait",
                "[Check for response...]",
                conditionDelegate: () => true,
                consequenceDelegate: null);

            // Proxy evaluation node: 
            // If they click check and it's NOT ready, it jumps here and NOW shows "..."
            starter.AddDialogLine(
                "lothbrok_chat_evaluate_wait_not_ready",
                "lothbrok_chat_evaluate_wait",
                "lothbrok_chat_wait_options",
                "...", 
                conditionDelegate: () => _isAIGenerating,
                consequenceDelegate: null);

            // If it IS ready, it sets the new text and jumps to the normal options
            starter.AddDialogLine(
                "lothbrok_chat_evaluate_wait_ready",
                "lothbrok_chat_evaluate_wait",
                "lothbrok_chat_options",
                "{GENERATED_NPC_TEXT}",
                conditionDelegate: () => {
                    if (_isAIGenerating) return false;
                    MBTextManager.SetTextVariable("GENERATED_NPC_TEXT", _lastGeneratedResponse);
                    return true;
                },
                consequenceDelegate: () => {
                    if (!string.IsNullOrEmpty(_lastGeneratedActionText))
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            _lastGeneratedActionText, new Color(0.8f, 0.4f, 1f)));
                        _lastGeneratedActionText = "";
                    }
                });
        }

        // ================================================================
        // BACKGROUND PROCESSING
        // ================================================================

        /// <summary>
        /// Runs on a background thread to prevent game lock-up
        /// while waiting for the LLM API.
        /// </summary>
        private void ProcessConversation(Hero npc, Hero player, string playerText)
        {
            try
            {
                LothbrokSubModule.Log($"ProcessConversation START: player said '{playerText}' to {npc.Name}");

                // 1. Build the prompt
                var builtPrompt = ContextAssembler.AssembleForConversation(npc, player, playerText);
                LothbrokSubModule.Log($"Prompt built: System={builtPrompt.SystemPrompt.Length}c, User={builtPrompt.UserMessage.Length}c");

                // 2. Call the API
                var response = APIRouter.SendChatCompletion(
                    builtPrompt.SystemPrompt,
                    builtPrompt.UserMessage);

                if (!response.Success)
                {
                    LothbrokSubModule.Log($"API FAILED: {response.Error}", Debug.DebugColor.Red);
                    _lastGeneratedResponse = "[The words would not come. Error: " + response.Error + "]";

                    // Show red banner so player knows something went wrong
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[LothbrokAI] API Error — click [Check for response] to see details.",
                        new Color(1f, 0.3f, 0.3f)));
                }
                else
                {
                    LothbrokSubModule.Log($"API SUCCESS: {response.LatencyMs}ms, {response.TotalTokens} tokens");

                    // 3. Parse response
                    var parsed = ResponseProcessor.Parse(response.Text);
                    _lastGeneratedResponse = parsed.DialogueText;

                    string npcId = ContextAssembler.GetNpcId(npc);
                    int gameDay = (int)CampaignTime.Now.ToDays;

                    // 4. Save to memory
                    MemoryEngine.Store(npcId, npc.Name.ToString(), playerText, parsed.DialogueText, gameDay);

                    // 5. Queue actions for main thread
                    if (parsed.Actions.Count > 0)
                    {
                        QueueActionsForMainThread(parsed.Actions, npc, player);
                    }

                    // Show green banner so player knows response is ready
                    InformationManager.DisplayMessage(new InformationMessage(
                        $"[LothbrokAI] {npc.Name} is ready to speak. Click [Check for response].",
                        new Color(0.4f, 1f, 0.4f)));
                }
            }
            catch (Exception ex)
            {
                LothbrokSubModule.LogError("ProcessConversation", ex);
                _lastGeneratedResponse = "[I seem to have lost my train of thought. Error: " + ex.Message + "]";
            }
            finally
            {
                _isAIGenerating = false;
            }
        }

        // ================================================================
        // ACTION QUEUE (main thread execution)
        // ================================================================

        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _mainThreadQueue =
            new System.Collections.Concurrent.ConcurrentQueue<Action>();

        private void QueueActionsForMainThread(System.Collections.Generic.List<NpcAction> actions, Hero npc, Hero player)
        {
            _mainThreadQueue.Enqueue(() => {
                var result = ActionEngine.ProcessActions(actions, npc, player);
                if (result.HasExecuted)
                {
                    _lastGeneratedActionText = "Actions taken: " + string.Join(", ", result.Executed);
                }
            });
        }

        /// <summary>
        /// Called from the main thread tick to process queued actions safely.
        /// </summary>
        public static void ProcessMainThreadActions()
        {
            while (_mainThreadQueue.TryDequeue(out Action action))
            {
                action.Invoke();
            }
        }
    }
}
