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
    /// DESIGN: This intercepts vanilla dialogue under specific conditions,
    /// hands the prompt to the APIRouter, parses the LLM output, and injects
    /// it back into the game window.
    /// </summary>
    public class DialogueInterceptor : CampaignBehaviorBase
    {
        public static string PlayerInputText = "";

        private bool _isAIGenerating = false;
        private string _lastGeneratedResponse = "";
        private string _lastGeneratedActionText = "";

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // SyncData for the mod is mostly handled by SaveManager, but
            // we could store current conversation states here.
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            // Inject our dialogue entry point into the game's trees
            AddLothbrokDialogues(starter);
        }

        private void AddLothbrokDialogues(CampaignGameStarter starter)
        {
            // ================================================================
            // ENTRY POINT
            // ================================================================

            // 1. Player option to start AI chat (appears in the first screen of vanilla dialogue)
            starter.AddPlayerLine(
                "lothbrok_chat_start",
                "hero_main_options",       // The main vanilla dialogue node
                "lothbrok_chat_input",     // The node we jump to
                "{=lothbrok_talk_opt}I wish to speak with you on a matter...",
                conditionDelegate: () => Hero.OneToOneConversationHero != null,
                consequenceDelegate: () => {
                    // Initialize first contact if needed
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
                    InformationManager.ShowInquiry(new InquiryData("Test Results", result, true, false, "Close", "", null, null));
                });

            // DEBUG: Map Calradia Graph
            starter.AddPlayerLine(
                "lothbrok_chat_debug_graph",
                "hero_main_options",       
                "hero_main_options",     
                "[DEBUG] Map Calradia Graph (Graph DB Export)",
                conditionDelegate: () => Hero.OneToOneConversationHero != null,
                consequenceDelegate: () => {
                    string logDir = System.IO.Path.Combine(LothbrokSubModule.ModDir, "logs");
                    if (!System.IO.Directory.Exists(logDir)) System.IO.Directory.CreateDirectory(logDir);
                    string path = System.IO.Path.Combine(logDir, "calradia_graph.json");
                    string result = LothbrokAI.Core.CalradiaGraphExporter.ExportGraph(path);
                    InformationManager.ShowInquiry(new InquiryData("Graph Export", result, true, false, "Close", "", null, null));
                });

            // ================================================================
            // INPUT NODE
            // ================================================================

            // 2. The NPC waits for input
            starter.AddDialogLine(
                "lothbrok_chat_input",
                "lothbrok_chat_input",
                "lothbrok_chat_options",
                "{=lothbrok_listening}I am listening...",
                conditionDelegate: () => true,
                consequenceDelegate: null);

            // ================================================================
            // TEXT ENTRY
            // ================================================================

            // 3. The player chooses to type
            starter.AddPlayerLine(
                "lothbrok_chat_type",
                "lothbrok_chat_options",
                "lothbrok_chat_generating",
                "{=lothbrok_type_opt}[Type a response]",
                conditionDelegate: () => true,
                consequenceDelegate: () => {
                    _isAIGenerating = true;
                    _lastGeneratedResponse = "Thinking...";
                    
                    var npc = Hero.OneToOneConversationHero;
                    var player = Hero.MainHero;

                    InformationManager.ShowTextInquiry(new TextInquiryData(
                        "Speak to " + npc.Name,
                        "What do you wish to say?",
                        true, true,
                        "Speak", "Cancel",
                        text => {
                            PlayerInputText = text;
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                Task.Run(() => ProcessConversation(npc, player, text));
                            }
                            else
                            {
                                _isAIGenerating = false;
                                _lastGeneratedResponse = "[Silence]";
                            }
                        },
                        () => {
                            // Cancelled
                            _isAIGenerating = false;
                            _lastGeneratedResponse = "[I changed my mind.]";
                        }
                    ));
                });

            // ================================================================
            // GENERATION WAITING LOOP
            // ================================================================

            // 4. The NPC is "thinking" while the async task runs or inquiry is open
            starter.AddDialogLine(
                "lothbrok_chat_generating",
                "lothbrok_chat_generating",
                "lothbrok_chat_wait_loop",
                "...",
                conditionDelegate: () => _isAIGenerating,
                consequenceDelegate: null);

            starter.AddPlayerLine(
                "lothbrok_chat_wait_loop",
                "lothbrok_chat_wait_loop",
                "lothbrok_chat_generating",
                "[Wait for response...]",
                conditionDelegate: () => _isAIGenerating,
                consequenceDelegate: null);

            starter.AddPlayerLine(
                "lothbrok_chat_wait_cancel",
                "lothbrok_chat_wait_loop",
                "close_window", // Fail-safe exit if API hangs
                "[Cancel and leave]",
                conditionDelegate: () => _isAIGenerating,
                consequenceDelegate: () => { _isAIGenerating = false; });

            // ================================================================
            // NPC RESPONSE
            // ================================================================

            // 5. When generation finishes, deliver the text
            starter.AddDialogLine(
                "lothbrok_chat_npc_reply",
                "lothbrok_chat_generating",
                "lothbrok_chat_loop_options",
                "{GENERATED_NPC_TEXT}",
                conditionDelegate: () => {
                    MBTextManager.SetTextVariable("GENERATED_NPC_TEXT", _lastGeneratedResponse);
                    return !_isAIGenerating; // Only available when generation is done
                },
                consequenceDelegate: () => {
                    if (!string.IsNullOrEmpty(_lastGeneratedActionText))
                    {
                        InformationManager.DisplayMessage(new InformationMessage(
                            _lastGeneratedActionText, new TaleWorlds.Library.Color(0.8f, 0.4f, 1f)));
                        _lastGeneratedActionText = "";
                    }
                });

            // ================================================================
            // LOOP OR LEAVE
            // ================================================================

            // 6a. Option to reply again
            starter.AddPlayerLine(
                "lothbrok_chat_continue",
                "lothbrok_chat_loop_options",
                "lothbrok_chat_generating", // Jump directly to generate via inquiry
                "{=lothbrok_reply_opt}[Reply]",
                conditionDelegate: () => true,
                consequenceDelegate: () => {
                    _isAIGenerating = true;
                    _lastGeneratedResponse = "Thinking...";
                    
                    var npc = Hero.OneToOneConversationHero;
                    var player = Hero.MainHero;

                    InformationManager.ShowTextInquiry(new TextInquiryData(
                        "Speak to " + npc.Name,
                        "What do you wish to say?",
                        true, true,
                        "Speak", "Cancel",
                        text => {
                            PlayerInputText = text;
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                Task.Run(() => ProcessConversation(npc, player, text));
                            }
                            else
                            {
                                _isAIGenerating = false;
                                _lastGeneratedResponse = "[Silence]";
                            }
                        },
                        () => {
                            _isAIGenerating = false;
                            _lastGeneratedResponse = "[Silence]";
                        }
                    ));
                });

            // 6b. Option to return to vanilla dialogue options (only for lords)
            starter.AddPlayerLine(
                "lothbrok_chat_return",
                "lothbrok_chat_loop_options",
                "hero_main_options", // Loop back to vanilla
                "{=lothbrok_leave_opt}[Return to normal matters]",
                conditionDelegate: () => Hero.OneToOneConversationHero != null && Hero.OneToOneConversationHero.IsLord,
                consequenceDelegate: null);

            // 6c. Universal exit
            starter.AddPlayerLine(
                "lothbrok_chat_leave",
                "lothbrok_chat_loop_options",
                "close_window",
                "{=lothbrok_leave_opt}[Leave]",
                conditionDelegate: () => true,
                consequenceDelegate: null);
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
                // 1. Build the prompt via ContextAssembler
                var builtPrompt = ContextAssembler.AssembleForConversation(npc, player, playerText);

                // 2. Transmit to active API
                var response = APIRouter.SendChatCompletion(
                    builtPrompt.SystemPrompt,
                    builtPrompt.UserMessage);

                if (!response.Success)
                {
                    _lastGeneratedResponse = "[Failed to connect to mind: " + response.Error + "]";
                }
                else
                {
                    // 3. Parse text and extract structured actions
                    var parsed = ResponseProcessor.Parse(response.Text);
                    _lastGeneratedResponse = parsed.DialogueText;

                    string npcId = ContextAssembler.GetNpcId(npc);
                    int gameDay = (int)CampaignTime.Now.ToDays;

                    // 4. Save to MemoryEngine graph
                    MemoryEngine.Store(npcId, npc.Name.ToString(), playerText, parsed.DialogueText, gameDay);

                    // 5. Execute Action Engine (Game Decides, AI narrates)
                    // Note: Actions altering game state MUST run on main thread!
                    if (parsed.Actions.Count > 0)
                    {
                        // Safely queue action execution onto the main game thread
                        TaleWorlds.Library.InformationManager.DisplayMessage(
                            new TaleWorlds.Library.InformationMessage("Processing actions..."));
                        
                        // We store the actions and flag to execute them on the next tick
                        // via CampaignEvents.TickEvent (implemented in SubModule or Patches)
                        QueueActionsForMainThread(parsed.Actions, npc, player);
                    }
                }
            }
            catch (Exception ex)
            {
                LothbrokSubModule.Log("Error in ProcessConversation: " + ex.Message, Debug.DebugColor.Red);
                _lastGeneratedResponse = "[I seem to have lost my train of thought. Error: " + ex.Message + "]";
            }
            finally
            {
                _isAIGenerating = false;
            }
        }

        // Action queue for main thread execution
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

        public static void ProcessMainThreadActions()
        {
            while (_mainThreadQueue.TryDequeue(out Action action))
            {
                action.Invoke();
            }
        }
    }
}
