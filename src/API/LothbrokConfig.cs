using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace LothbrokAI.API
{
    /// <summary>
    /// Configuration for LothbrokAI mod settings.
    /// 
    /// DESIGN: All settings are in a single JSON file that's hot-reloadable.
    /// Users can edit config.json while the game is running and settings will
    /// be picked up on next conversation. API keys are stored locally, never
    /// transmitted anywhere except to the configured backend.
    /// </summary>
    public class LothbrokConfig
    {
        // ================================================================
        // API BACKEND SETTINGS
        // ================================================================

        /// <summary>Which backend to use: "openrouter", "ollama", "lmstudio", "koboldcpp", "deepseek"</summary>
        [JsonProperty("backend")]
        public string Backend { get; set; } = "openrouter";

        /// <summary>OpenRouter API key</summary>
        [JsonProperty("openrouter_api_key")]
        public string OpenRouterApiKey { get; set; } = "";

        /// <summary>OpenRouter model ID (e.g. "anthropic/claude-3.5-sonnet")</summary>
        [JsonProperty("openrouter_model")]
        public string OpenRouterModel { get; set; } = "anthropic/claude-3.5-sonnet";

        /// <summary>DeepSeek API key</summary>
        [JsonProperty("deepseek_api_key")]
        public string DeepSeekApiKey { get; set; } = "";

        /// <summary>DeepSeek model ID</summary>
        [JsonProperty("deepseek_model")]
        public string DeepSeekModel { get; set; } = "deepseek-chat";

        /// <summary>Ollama/LM Studio base URL (e.g. "http://192.168.1.100:11434")</summary>
        [JsonProperty("local_api_url")]
        public string LocalApiUrl { get; set; } = "http://localhost:11434";

        /// <summary>Local model name for Ollama/LM Studio</summary>
        [JsonProperty("local_model")]
        public string LocalModel { get; set; } = "llama3";

        /// <summary>KoboldCpp base URL</summary>
        [JsonProperty("koboldcpp_url")]
        public string KoboldCppUrl { get; set; } = "http://localhost:5001";

        // ================================================================
        // EMBEDDING SETTINGS
        // ================================================================

        /// <summary>Embedding backend: "openrouter", "lmstudio", "none" (falls back to TF-IDF)</summary>
        [JsonProperty("embedding_backend")]
        public string EmbeddingBackend { get; set; } = "lmstudio";

        /// <summary>Embedding model (for LM Studio or OpenRouter)</summary>
        [JsonProperty("embedding_model")]
        public string EmbeddingModel { get; set; } = "nomic-embed-text";

        /// <summary>LM Studio embedding endpoint</summary>
        [JsonProperty("embedding_url")]
        public string EmbeddingUrl { get; set; } = "http://192.168.1.100:1234";

        // ================================================================
        // GENERATION SETTINGS
        // ================================================================

        /// <summary>Temperature for NPC responses (0.0 = deterministic, 1.0 = creative)</summary>
        [JsonProperty("temperature")]
        public float Temperature { get; set; } = 0.8f;

        /// <summary>Max tokens for NPC response</summary>
        [JsonProperty("max_response_tokens")]
        public int MaxResponseTokens { get; set; } = 800;

        /// <summary>Max total prompt tokens (input + output)</summary>
        [JsonProperty("max_prompt_tokens")]
        public int MaxPromptTokens { get; set; } = 4000;

        /// <summary>Request timeout in seconds</summary>
        [JsonProperty("timeout_seconds")]
        public int TimeoutSeconds { get; set; } = 30;

        // ================================================================
        // MEMORY SETTINGS
        // ================================================================

        /// <summary>Number of relevant memories to retrieve per conversation</summary>
        [JsonProperty("memory_top_k")]
        public int MemoryTopK { get; set; } = 5;

        /// <summary>Number of recent raw messages to include</summary>
        [JsonProperty("recent_messages_count")]
        public int RecentMessagesCount { get; set; } = 4;

        /// <summary>Summarize after this many new messages</summary>
        [JsonProperty("summary_trigger_count")]
        public int SummaryTriggerCount { get; set; } = 10;

        /// <summary>Max embeddings stored per NPC (older ones pruned)</summary>
        [JsonProperty("max_embeddings_per_npc")]
        public int MaxEmbeddingsPerNpc { get; set; } = 500;

        // ================================================================
        // HYPERGRAPH MEMORY SETTINGS (v2)
        // ================================================================

        /// <summary>
        /// Enable hypergraph context engine. Conversations build a persistent
        /// concept graph that captures emergent context patterns across NPCs.
        /// </summary>
        [JsonProperty("hypergraph_enabled")]
        public bool HypergraphEnabled { get; set; } = true;

        /// <summary>
        /// Enable cross-NPC semantic retrieval. Memories from all NPCs are
        /// searchable during any conversation. Enables emergent connections
        /// (e.g., NPC B references behavior patterns from your NPC A conversations).
        /// </summary>
        [JsonProperty("cross_npc_retrieval")]
        public bool CrossNpcRetrieval { get; set; } = true;

        /// <summary>
        /// Minimum co-occurrence count before a hyperedge is considered
        /// significant enough to influence retrieval scoring.
        /// </summary>
        [JsonProperty("hyperedge_min_activations")]
        public int HyperedgeMinActivations { get; set; } = 2;

        /// <summary>
        /// Max hyperedges to inject as context chains into the prompt.
        /// Each activated edge adds ~15-25 tokens.
        /// </summary>
        [JsonProperty("max_context_chains")]
        public int MaxContextChains { get; set; } = 4;

        /// <summary>
        /// Semantic retrieval score weights. Must sum to 1.0.
        /// </summary>
        [JsonProperty("semantic_weight")]
        public float SemanticWeight { get; set; } = 0.7f;

        [JsonProperty("recency_weight")]
        public float RecencyWeight { get; set; } = 0.2f;

        [JsonProperty("same_npc_weight")]
        public float SameNpcWeight { get; set; } = 0.1f;

        // ================================================================
        // DEBUG SETTINGS
        // ================================================================

        /// <summary>Log full prompts to logs/prompts/ directory</summary>
        [JsonProperty("log_prompts")]
        public bool LogPrompts { get; set; } = true;

        /// <summary>Enable debug logging (verbose)</summary>
        [JsonProperty("debug_mode")]
        public bool DebugMode { get; set; } = false;

        // ================================================================
        // LOADING
        // ================================================================

        private static string _configPath;
        private static LothbrokConfig _instance;
        private static DateTime _lastModified;

        /// <summary>
        /// Get current config. Hot-reloads from disk if file was modified.
        /// </summary>
        public static LothbrokConfig Current
        {
            get
            {
                if (_instance == null)
                    return new LothbrokConfig(); // defaults

                // Hot-reload check
                try
                {
                    if (_configPath != null)
                    {
                        var lastWrite = System.IO.File.GetLastWriteTimeUtc(_configPath);
                        if (lastWrite > _lastModified)
                        {
                            LothbrokSubModule.Log("Config file changed, reloading...");
                            Load(_configPath);
                        }
                    }
                }
                catch { /* If we can't check, use cached */ }

                return _instance;
            }
        }

        /// <summary>
        /// Load config from JSON file. If file doesn't exist, creates with defaults.
        /// </summary>
        public static void Load(string path)
        {
            _configPath = path;

            try
            {
                if (System.IO.File.Exists(path))
                {
                    string json = System.IO.File.ReadAllText(path);
                    _instance = JsonConvert.DeserializeObject<LothbrokConfig>(json);
                    _lastModified = System.IO.File.GetLastWriteTimeUtc(path);
                    LothbrokSubModule.Log("Config loaded from: " + path);
                }
                else
                {
                    // Create default config
                    _instance = new LothbrokConfig();
                    string json = JsonConvert.SerializeObject(_instance, Formatting.Indented);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    System.IO.File.WriteAllText(path, json);
                    _lastModified = System.IO.File.GetLastWriteTimeUtc(path);
                    LothbrokSubModule.Log("Default config created at: " + path);
                }
            }
            catch (Exception ex)
            {
                LothbrokSubModule.Log("ERROR loading config: " + ex.Message,
                    TaleWorlds.Library.Debug.DebugColor.Red);
                _instance = new LothbrokConfig();
            }
        }
    }
}
