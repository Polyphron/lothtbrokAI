using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace LothbrokAI
{
    /// <summary>
    /// LothbrokAI entry point.
    /// 
    /// A standalone Bannerlord AI companion mod with semantic memory,
    /// smart context assembly, and proper data lifecycle management.
    /// 
    /// DESIGN: This replaces AIInfluence entirely. Key improvements:
    /// - Per-NPC vector memory instead of full history dumps
    /// - Token-budgeted prompts (4K max vs AIInfluence's 40K+)
    /// - Async API calls (no game thread blocking)
    /// - Diplomacy designed for 80+ kingdoms (Separatism compatible)
    /// - Data hygiene (no unbounded growth)
    /// </summary>
    public class LothbrokSubModule : MBSubModuleBase
    {
        public const string MOD_ID = "com.lothbrok.ai";
        public const string MOD_VERSION = "0.1.0";
        public const string LOG_PREFIX = "[LothbrokAI]";

        private Harmony _harmony;
        private static string _modDir;

        /// <summary>
        /// Get the mod's installation directory (Modules/LothbrokAI/).
        /// </summary>
        public static string ModDir { get { return _modDir; } }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            _harmony = new Harmony(MOD_ID);

            // Determine mod directory
            _modDir = System.IO.Path.GetDirectoryName(
                System.IO.Path.GetDirectoryName(
                    typeof(LothbrokSubModule).Assembly.Location));

            Log("LothbrokAI v" + MOD_VERSION + " loading...", Debug.DebugColor.Cyan);
            Log("Mod directory: " + _modDir);

            // ── Load configuration (hot-reloadable) ──
            string configPath = System.IO.Path.Combine(_modDir, "data", "config.json");
            API.LothbrokConfig.Load(configPath);

            // ── Initialize prompt templates ──
            string dataDir = System.IO.Path.Combine(_modDir, "data");
            Core.PromptBuilder.Initialize(dataDir);

            // ── Vanilla bug fix patches (ported from AIInfluence) ──
            try
            {
                Patches.VanillaBugFixes.ApplyManualPatches(_harmony);
                Log("Vanilla bug fixes registered.", Debug.DebugColor.Green);
            }
            catch (Exception ex)
            {
                Log("WARN: Bug fix patches failed: " + ex.Message, Debug.DebugColor.Yellow);
            }

            // Apply attribute-based patches
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log("LothbrokAI v" + MOD_VERSION + " loaded successfully.", Debug.DebugColor.Green);
        }

        /// <summary>
        /// Register campaign behaviors when a game starts.
        /// </summary>
        protected override void OnGameStart(Game game, IGameStarter gameStarter)
        {
            base.OnGameStart(game, gameStarter);

            if (game.GameType is Campaign)
            {
                var starter = gameStarter as CampaignGameStarter;
                if (starter != null)
                {
                    // Register campaign behaviors
                    starter.AddBehavior(new Core.DialogueInterceptor());
                    starter.AddBehavior(new Core.SaveBehavior());
                    starter.AddBehavior(new Medici.MediciManager());
                    starter.AddBehavior(new Systems.LothbrokDiplomacyBehavior());
                    Log("Campaign started. Engine, Save bindings, Diplomacy hooks, and Medici hooks initialized.", Debug.DebugColor.Green);
                    
                    // Boot the Local Game Master Server
                    API.LocalGameMasterServer.StartServer();
                }
            }
        }

        public override void OnGameInitializationFinished(Game game)
        {
            base.OnGameInitializationFinished(game);
            if (game.GameType is Campaign)
            {
                // Ensure the campaign has a unique ID, fallback to random GUID if vanilla fails for some reason
                string gameId = Campaign.Current.UniqueGameId;
                if (string.IsNullOrEmpty(gameId)) gameId = "sandbox_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                
                string saveDir = System.IO.Path.Combine(_modDir, "save_data", gameId);
                Memory.MemoryEngine.Initialize(saveDir);
                Log($"Memory Engine initialized for campaign ID: {gameId}", Debug.DebugColor.Green);
            }
        }

        protected override void OnSubModuleUnloaded()
        {
            // Flush all NPC memory files before unloading
            Memory.MemoryEngine.FlushAll();
            _harmony?.UnpatchAll(MOD_ID);
            
            // Kill the REST API Server
            API.LocalGameMasterServer.StopServer();
            
            Log("LothbrokAI unloaded.", Debug.DebugColor.Cyan);
            base.OnSubModuleUnloaded();
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            
            // Process any dialogue actions that need main-thread execution
            Core.DialogueInterceptor.ProcessMainThreadActions();
            Core.ActionEngine.ProcessQueuedActions();
            
            // Process any Omni-Tool commands pushed via the REST API
            API.LocalGameMasterServer.ProcessQueuedMainThreadActions();
        }

        // ================================================================
        // LOGGING
        // ================================================================

        /// <summary>
        /// Centralized logging with consistent prefix.
        /// Writes to Native Bannerlord Debug Console and lothbrok.log
        /// </summary>
        public static void Log(string message, Debug.DebugColor color = Debug.DebugColor.White)
        {
            // Native Engine Logging
            Debug.Print($"{LOG_PREFIX} {message}", 0, color);

            // Dedicated File Logging
            try
            {
                if (!string.IsNullOrEmpty(_modDir))
                {
                    string logDir = System.IO.Path.Combine(_modDir, "logs");
                    if (!System.IO.Directory.Exists(logDir))
                    {
                        System.IO.Directory.CreateDirectory(logDir);
                    }
                    string logFile = System.IO.Path.Combine(logDir, "lothbrok.log");
                    string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    System.IO.File.AppendAllText(logFile, $"[{timestamp}] {message}\n");
                }
            }
            catch 
            {
                // Failsafe: if we can't write to the log, we don't crash the game
            }
        }
        
        /// <summary>
        /// Dedicated Exception Logger tracking full stack traces.
        /// </summary>
        public static void LogError(string context, Exception ex)
        {
            Log($"[ERROR] {context}: {ex.Message}", Debug.DebugColor.Red);
            try
            {
                if (!string.IsNullOrEmpty(_modDir))
                {
                    string logDir = System.IO.Path.Combine(_modDir, "logs");
                    if (!System.IO.Directory.Exists(logDir)) System.IO.Directory.CreateDirectory(logDir);
                    string logFile = System.IO.Path.Combine(logDir, "lothbrok_errors.log");
                    string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    string errorMessage = $"[{timestamp}] {context}\nMESSAGE: {ex.Message}\nSTACKTRACE: {ex.StackTrace}\n-----------------------------------\n";
                    System.IO.File.AppendAllText(logFile, errorMessage);
                }
            }
            catch {}
        }
    }
}
