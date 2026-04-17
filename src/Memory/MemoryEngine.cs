using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace LothbrokAI.Memory
{
    /// <summary>
    /// Per-NPC conversation log. Append-only, never pruned.
    /// Used for re-embedding, summary regeneration, and export.
    /// 
    /// DESIGN: Separate from the memory file so raw history is preserved
    /// even if embeddings/summaries are regenerated. This is the source
    /// of truth for what was actually said.
    /// </summary>
    public class ConversationEntry
    {
        [JsonProperty("role")]
        public string Role { get; set; } // "player" or "npc"

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        [JsonProperty("gameDay")]
        public int GameDay { get; set; }
    }

    /// <summary>
    /// Embedded memory node with tags for context spreading.
    /// </summary>
    public class MemoryNode
    {
        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        [JsonProperty("vector")]
        public float[] Vector { get; set; } // null if TF-IDF phase

        [JsonProperty("createdAt")]
        public string CreatedAt { get; set; }

        [JsonProperty("tfidfScore")]
        public float TfidfScore { get; set; } // Pre-computed for Phase 1
    }

    /// <summary>
    /// Complete NPC memory file format.
    /// Stored as {npcId}.lothbrok.json
    /// </summary>
    public class NpcMemoryFile
    {
        [JsonProperty("npcId")]
        public string NpcId { get; set; }

        [JsonProperty("npcName")]
        public string NpcName { get; set; }

        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        // ── Generated identity ──
        [JsonProperty("personality")]
        public string Personality { get; set; }

        [JsonProperty("backstory")]
        public string Backstory { get; set; }

        [JsonProperty("speechQuirks")]
        public string SpeechQuirks { get; set; }

        // ── Rolling summary ──
        [JsonProperty("summary")]
        public MemorySummary Summary { get; set; }

        // ── Full conversation log ──
        [JsonProperty("conversationLog")]
        public List<ConversationEntry> ConversationLog { get; set; } = new List<ConversationEntry>();

        // ── Embedded memories (Phase 1: TF-IDF, Phase 2: vectors) ──
        [JsonProperty("memories")]
        public List<MemoryNode> Memories { get; set; } = new List<MemoryNode>();

        // ── Relationship state ──
        [JsonProperty("metadata")]
        public NpcMetadata Metadata { get; set; } = new NpcMetadata();
    }

    public class MemorySummary
    {
        [JsonProperty("text")]
        public string Text { get; set; } = "";

        [JsonProperty("updatedAt")]
        public string UpdatedAt { get; set; }

        [JsonProperty("coversMessages")]
        public int CoversMessages { get; set; }
    }

    public class NpcMetadata
    {
        [JsonProperty("interactionCount")]
        public int InteractionCount { get; set; }

        [JsonProperty("trustLevel")]
        public float TrustLevel { get; set; } = 0.5f;

        [JsonProperty("romanceLevel")]
        public float RomanceLevel { get; set; }

        [JsonProperty("emotionalState")]
        public string EmotionalState { get; set; } = "neutral";

        [JsonProperty("lastInteraction")]
        public string LastInteraction { get; set; }

        [JsonProperty("firstMet")]
        public string FirstMet { get; set; }

        [JsonProperty("visitedSettlements")]
        public List<string> VisitedSettlements { get; set; } = new List<string>();
    }

    /// <summary>
    /// Memory retrieval result — what gets injected into the prompt.
    /// </summary>
    public class MemoryPayload
    {
        public string Summary { get; set; }
        public List<string> RelevantMemories { get; set; }
        public List<string> RecentMessages { get; set; }
        /// <summary>
        /// Activated hyperedge context chains from the hypergraph engine.
        /// Injected as [CONTEXT CHAINS] prompt section.
        /// Empty when hypergraph is disabled or no edges fired.
        /// </summary>
        public List<string> ContextChains { get; set; } = new List<string>();
        public NpcMetadata Metadata { get; set; }
        public string Personality { get; set; }
        public string Backstory { get; set; }
        public string SpeechQuirks { get; set; }
    }

    /// <summary>
    /// Orchestrator for NPC memory: retrieve, store, summarize.
    /// 
    /// Phase 1: TF-IDF keyword retrieval (ported from our hotfix patch).
    /// Phase 2: Vector embeddings via remote API (LM Studio / OpenRouter).
    /// 
    /// DESIGN: Memory files are per-NPC JSON sidecars. They're loaded
    /// on-demand when a conversation starts and cached in memory while
    /// the conversation is active. Saved after conversation ends.
    /// </summary>
    public static class MemoryEngine
    {
        private static System.Collections.Concurrent.ConcurrentDictionary<string, NpcMemoryFile> _cache
            = new System.Collections.Concurrent.ConcurrentDictionary<string, NpcMemoryFile>();

        private static string _saveDataDir;

        /// <summary>
        /// Initialize memory engine with save data directory.
        /// </summary>
        public static void Initialize(string saveDataDir)
        {
            _saveDataDir = saveDataDir;
            Directory.CreateDirectory(saveDataDir);
            LothbrokSubModule.Log("MemoryEngine initialized: " + saveDataDir);
        }

        // ================================================================
        // RETRIEVE
        // ================================================================

        /// <summary>
        /// Retrieve relevant memories for an NPC conversation.
        /// </summary>
        public static MemoryPayload Retrieve(string npcId, string npcName, string playerMessage)
        {
            var mem = LoadOrCreate(npcId, npcName);

            // Get recent messages (last N raw exchanges)
            int recentCount = API.LothbrokConfig.Current.RecentMessagesCount;
            var recent = new List<string>();
            int start = Math.Max(0, mem.ConversationLog.Count - (recentCount * 2));
            for (int i = start; i < mem.ConversationLog.Count; i++)
            {
                var entry = mem.ConversationLog[i];
                string prefix = entry.Role == "player" ? "Player" : npcName;
                recent.Add(prefix + ": " + entry.Text);
            }

            int topK = API.LothbrokConfig.Current.MemoryTopK;
            int currentGameDay = TaleWorlds.CampaignSystem.Campaign.Current != null
                ? (int)TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays
                : 0;

            List<string> relevant;

            // DESIGN: ZERO network calls during Retrieve(). The player is waiting.
            //
            // Old approach: called GetEmbedding(playerMessage) here — added 100-500ms
            // of network latency to the critical path before the LLM call even starts.
            //
            // New approach: use the LAST stored vector for this NPC as a rough query
            // vector (the most recent topic is likely the best proxy). If no vectors
            // exist yet (first few conversations), fall back to TF-IDF immediately.
            //
            // Precise query embedding happens during Store() AFTER the response is
            // shown to the player — no latency on the critical path.

            float[] queryVec = null;
            if (LothbrokDatabase.IsOpen)
            {
                // Use most recent stored vector for this NPC as query proxy
                queryVec = VectorIndex.GetLatestVector(npcId);
            }

            if (queryVec != null)
            {
                relevant = VectorIndex.Search(queryVec, npcId, currentGameDay, topK);

                // If semantic search returned nothing, fall back
                if (relevant.Count == 0)
                    relevant = RetrieveTfIdf(mem, playerMessage, topK);
            }
            else
            {
                relevant = RetrieveTfIdf(mem, playerMessage, topK);
            }

            // Spreading activation: which hyperedges does the current message trigger?
            List<string> contextChains = new List<string>();
            if (API.LothbrokConfig.Current.HypergraphEnabled && LothbrokDatabase.IsOpen)
            {
                var messageTags = ExtractTags(playerMessage);
                contextChains = HypergraphEngine.GetActivatedContextChains(npcId, messageTags);
            }

            return new MemoryPayload
            {
                Summary = mem.Summary != null ? mem.Summary.Text : null,
                RelevantMemories = relevant,
                RecentMessages = recent,
                ContextChains = contextChains,
                Metadata = mem.Metadata,
                Personality = mem.Personality,
                Backstory = mem.Backstory,
                SpeechQuirks = mem.SpeechQuirks
            };
        }

        // ================================================================
        // STORE
        // ================================================================

        /// <summary>
        /// Store a conversation exchange in NPC memory.
        /// </summary>
        public static void Store(
            string npcId, string npcName,
            string playerMessage, string npcResponse,
            int gameDay, List<string> tags = null)
        {
            var mem = LoadOrCreate(npcId, npcName);

            // Append to conversation log
            string timestamp = DateTime.UtcNow.ToString("o");
            
            MemoryNode node;
            
            lock (mem)
            {
                mem.ConversationLog.Add(new ConversationEntry
                {
                    Role = "player",
                    Text = playerMessage,
                    Timestamp = timestamp,
                    GameDay = gameDay
                });
                mem.ConversationLog.Add(new ConversationEntry
                {
                    Role = "npc",
                    Text = npcResponse,
                    Timestamp = timestamp,
                    GameDay = gameDay
                });

                // Create memory node for retrieval
                string combined = "Player: " + playerMessage + " | " + npcName + ": " + npcResponse;
                node = new MemoryNode
                {
                    Text = combined,
                    Tags = tags ?? ExtractTags(combined),
                    CreatedAt = timestamp
                };

                // Semantic embedding — stored in JSON for compat, also upserted to SQLite index
                float[] embedding = API.APIRouter.GetEmbedding(combined);
                if (embedding != null)
                {
                    node.Vector = embedding;
                }

                mem.Memories.Add(node);

                // Prune JSON store if over limit (SQLite has no per-NPC cap)
                int maxMemories = API.LothbrokConfig.Current.MaxEmbeddingsPerNpc;
                if (mem.Memories.Count > maxMemories)
                {
                    mem.Memories.RemoveRange(0, mem.Memories.Count - maxMemories);
                }

                // Update metadata
                mem.Metadata.InteractionCount++;
                mem.Metadata.LastInteraction = timestamp;

                // Check if summary needs regeneration
                int triggerCount = API.LothbrokConfig.Current.SummaryTriggerCount;
                int summarizedCount = mem.Summary != null ? mem.Summary.CoversMessages : 0;
                if (mem.ConversationLog.Count - summarizedCount >= triggerCount)
                {
                    RegenerateExtractSummary(mem);
                }
            }

            // Upsert to SQLite vector index (outside lock — no shared state)
            if (LothbrokDatabase.IsOpen)
            {
                string memId = npcId + "_" + timestamp.GetHashCode().ToString("X8");
                int currentDay = TaleWorlds.CampaignSystem.Campaign.Current != null
                    ? (int)TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays
                    : 0;
                string combined2 = "Player: " + playerMessage + " | " + npcName + ": " + npcResponse;
                var tagsForIndex = tags ?? ExtractTags(combined2);

                VectorIndex.Upsert(memId, npcId, npcName, combined2,
                    _cache.TryGetValue(npcId, out var m) ? m.Memories.LastOrDefault()?.Vector : null,
                    tagsForIndex, currentDay);

                // Feed the hypergraph
                if (API.LothbrokConfig.Current.HypergraphEnabled)
                {
                    string playerNpcId = TaleWorlds.CampaignSystem.Hero.MainHero?.StringId ?? "player";
                    HypergraphEngine.OnExchangeStored(npcId, npcName, playerNpcId, tagsForIndex);
                }
            }

            // Save to disk
            Save(mem);
        }

        /// <summary>
        /// Update NPC personality data (called by PersonalityGenerator).
        /// </summary>
        public static void SetPersonality(
            string npcId, string npcName,
            string personality, string backstory, string quirks)
        {
            var mem = LoadOrCreate(npcId, npcName);
            lock (mem)
            {
                mem.Personality = personality;
                mem.Backstory = backstory;
                mem.SpeechQuirks = quirks;
            }
            Save(mem);
        }

        // ================================================================
        // TF-IDF RETRIEVAL (Phase 1)
        // ================================================================

        /// <summary>
        /// TF-IDF keyword retrieval. Ported from our AIInfluenceHotfix
        /// ContextCompressor. Simple but effective for Phase 1.
        /// </summary>
        private static List<string> RetrieveTfIdf(
            NpcMemoryFile mem, string query, int topK)
        {
            if (mem.Memories.Count == 0) return new List<string>();

            // Extract query keywords
            var queryKeywords = ExtractKeywords(query);
            if (queryKeywords.Count == 0) return new List<string>();

            // Score each memory by keyword overlap
            var scored = new List<KeyValuePair<float, string>>();
            foreach (var node in mem.Memories)
            {
                var memKeywords = ExtractKeywords(node.Text);
                float score = ComputeKeywordOverlap(queryKeywords, memKeywords);

                // Tag bonus: memories with matching tags score higher
                foreach (string tag in node.Tags)
                {
                    if (query.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 0.3f;
                    }
                }

                if (score > 0)
                {
                    scored.Add(new KeyValuePair<float, string>(score, node.Text));
                }
            }

            // Sort by score descending, take top K
            scored.Sort((a, b) => b.Key.CompareTo(a.Key));
            var results = new List<string>();
            for (int i = 0; i < Math.Min(topK, scored.Count); i++)
            {
                results.Add(scored[i].Value);
            }

            return results;
        }

        // ================================================================
        // KEYWORD EXTRACTION
        // ================================================================

        private static readonly HashSet<string> StopWords = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "is", "are", "was", "were", "be", "been",
            "being", "have", "has", "had", "do", "does", "did", "will",
            "would", "could", "should", "may", "might", "shall", "can",
            "to", "of", "in", "for", "on", "with", "at", "by", "from",
            "as", "into", "through", "during", "before", "after", "above",
            "below", "between", "out", "off", "over", "under", "again",
            "further", "then", "once", "here", "there", "when", "where",
            "why", "how", "all", "each", "every", "both", "few", "more",
            "most", "other", "some", "such", "no", "not", "only", "own",
            "same", "so", "than", "too", "very", "just", "because", "but",
            "and", "or", "if", "while", "about", "that", "this", "these",
            "those", "i", "me", "my", "we", "our", "you", "your", "he",
            "him", "his", "she", "her", "it", "its", "they", "them",
            "their", "what", "which", "who", "whom"
        };

        private static List<string> ExtractKeywords(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<string>();

            var words = text.ToLowerInvariant()
                .Split(new[] { ' ', '.', ',', '!', '?', ':', ';', '-', '"',
                    '\'', '(', ')', '[', ']', '\n', '\r', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);

            var keywords = new List<string>();
            foreach (string word in words)
            {
                if (word.Length > 2 && !StopWords.Contains(word))
                {
                    keywords.Add(word);
                }
            }
            return keywords;
        }

        private static float ComputeKeywordOverlap(
            List<string> queryKeywords, List<string> memKeywords)
        {
            if (queryKeywords.Count == 0 || memKeywords.Count == 0) return 0;

            var memSet = new HashSet<string>(memKeywords, StringComparer.OrdinalIgnoreCase);
            int overlap = 0;
            foreach (string kw in queryKeywords)
            {
                if (memSet.Contains(kw)) overlap++;
            }

            // Normalized overlap score
            return (float)overlap / queryKeywords.Count;
        }

        private static List<string> ExtractTags(string text)
        {
            // DESIGN: Tags feed the hypergraph context engine. Two sources:
            //   1. Game-concept dictionary: detects semantic concepts regardless
            //      of capitalization ("I gave you gold" → CONCEPT_gold).
            //   2. Proper noun detection: catches NPC names, locations, kingdoms
            //      ("Dermot told me" → H_dermot linkage).
            //
            // This replaced the Phase 1 proper-noun-only extraction which
            // produced empty tag sets for ~90% of real player dialogue,
            // leaving the hypergraph inert.

            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Normalize for concept matching
            string lower = text.ToLowerInvariant();
            var words = lower.Split(new[] { ' ', '.', ',', '!', '?', ':', ';', '-',
                '"', '\'', '(', ')', '[', ']', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries);

            // --- PASS 1: Game-concept dictionary ---
            // Each concept keyword maps to a canonical concept tag.
            // Multiple surface forms can map to the same concept.
            foreach (string word in words)
            {
                string concept;
                if (GameConceptDictionary.TryGetValue(word, out concept))
                {
                    tags.Add(concept);
                }
            }

            // Multi-word concept detection (phrases)
            foreach (var phrase in GameConceptPhrases)
            {
                if (lower.Contains(phrase.Key))
                    tags.Add(phrase.Value);
            }

            // --- PASS 2: Proper nouns (NPC names, locations) ---
            var rawWords = text.Split(' ');
            foreach (string word in rawWords)
            {
                string clean = word.Trim('.', ',', '!', '?', ':', ';', '"', '\'');
                if (clean.Length > 2
                    && char.IsUpper(clean[0])
                    && !StopWords.Contains(clean.ToLowerInvariant()))
                {
                    tags.Add(clean.ToLowerInvariant());
                }
            }

            return new List<string>(tags);
        }

        // ================================================================
        // GAME CONCEPT DICTIONARY
        // ================================================================

        // DESIGN: Maps surface-form words to canonical concept tags.
        // Curated for the Bannerlord domain. Kept as a flat dictionary
        // for O(1) lookup per word — no regex, no NLP, no API calls.
        // This is what makes the hypergraph actually work.

        private static readonly Dictionary<string, string> GameConceptDictionary =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // --- Economy ---
            { "gold",       "gold" },
            { "coin",       "gold" },
            { "coins",      "gold" },
            { "denars",     "gold" },
            { "denar",      "gold" },
            { "money",      "gold" },
            { "pay",        "gold" },
            { "paid",       "gold" },
            { "payment",    "gold" },
            { "bribe",      "bribery" },
            { "bribed",     "bribery" },
            { "bribery",    "bribery" },
            { "gift",       "generosity" },
            { "generous",   "generosity" },
            { "trade",      "trade" },
            { "trading",    "trade" },
            { "merchant",   "trade" },
            { "goods",      "trade" },
            { "price",      "trade" },
            { "buy",        "trade" },
            { "sell",       "trade" },

            // --- War & Combat ---
            { "war",        "war" },
            { "battle",     "war" },
            { "fight",      "war" },
            { "fighting",   "war" },
            { "army",       "war" },
            { "siege",      "siege" },
            { "attack",     "war" },
            { "raid",       "raid" },
            { "raiding",    "raid" },
            { "sword",      "combat" },
            { "knight",     "combat" },
            { "soldier",    "war" },
            { "troops",     "war" },
            { "kill",       "violence" },
            { "killed",     "violence" },
            { "death",      "death" },
            { "dead",       "death" },
            { "died",       "death" },
            { "blood",      "violence" },
            { "revenge",    "revenge" },
            { "vengeance",  "revenge" },

            // --- Diplomacy & Politics ---
            { "peace",      "peace" },
            { "truce",      "peace" },
            { "alliance",   "alliance" },
            { "ally",       "alliance" },
            { "allies",     "alliance" },
            { "vassal",     "fealty" },
            { "king",       "royalty" },
            { "queen",      "royalty" },
            { "ruler",      "royalty" },
            { "throne",     "royalty" },
            { "kingdom",    "politics" },
            { "empire",     "politics" },
            { "rebel",      "rebellion" },
            { "rebellion",  "rebellion" },
            { "overthrow",  "rebellion" },
            { "power",      "power" },
            { "influence",  "power" },
            { "court",      "politics" },
            { "council",    "politics" },
            { "vote",       "politics" },
            { "election",   "politics" },
            { "conspiracy", "conspiracy" },
            { "plot",       "conspiracy" },
            { "scheme",     "conspiracy" },

            // --- Trust & Relationships ---
            { "trust",      "trust" },
            { "trusted",    "trust" },
            { "distrust",   "distrust" },
            { "betray",     "betrayal" },
            { "betrayal",   "betrayal" },
            { "betrayed",   "betrayal" },
            { "treachery",  "betrayal" },
            { "loyal",      "loyalty" },
            { "loyalty",    "loyalty" },
            { "oath",       "loyalty" },
            { "promise",    "promise" },
            { "honor",      "honor" },
            { "honour",     "honor" },
            { "friend",     "friendship" },
            { "friendship", "friendship" },
            { "enemy",      "enmity" },
            { "enemies",    "enmity" },
            { "hate",       "enmity" },
            { "hatred",     "enmity" },
            { "rival",      "rivalry" },
            { "respect",    "respect" },
            { "fear",       "fear" },
            { "afraid",     "fear" },
            { "threat",     "threat" },
            { "threaten",   "threat" },
            { "lie",        "deception" },
            { "lying",      "deception" },
            { "liar",       "deception" },
            { "secret",     "secrets" },
            { "secrets",    "secrets" },
            { "spy",        "espionage" },
            { "spying",     "espionage" },
            { "informant",  "espionage" },

            // --- Romance & Family ---
            { "love",       "romance" },
            { "romance",    "romance" },
            { "romantic",   "romance" },
            { "kiss",       "romance" },
            { "heart",      "romance" },
            { "beautiful",  "attraction" },
            { "handsome",   "attraction" },
            { "eyes",       "attraction" },
            { "marry",      "marriage" },
            { "marriage",   "marriage" },
            { "wife",       "marriage" },
            { "husband",    "marriage" },
            { "wedding",    "marriage" },
            { "child",      "family" },
            { "children",   "family" },
            { "son",        "family" },
            { "daughter",   "family" },
            { "father",     "family" },
            { "mother",     "family" },
            { "brother",    "family" },
            { "sister",     "family" },
            { "heir",       "succession" },
            { "inheritance","succession" },
            { "bloodline",  "succession" },

            // --- Assistance & Quests ---
            { "help",       "assistance" },
            { "quest",      "quest" },
            { "task",       "quest" },
            { "mission",    "quest" },
            { "favor",      "favor" },
            { "favour",     "favor" },
            { "reward",     "reward" },
            { "rescue",     "rescue" },
            { "protect",    "protection" },
            { "escort",     "protection" },
            { "guard",      "protection" },

            // --- Social Class & Identity ---
            { "lord",       "nobility" },
            { "lady",       "nobility" },
            { "noble",      "nobility" },
            { "peasant",    "commoners" },
            { "bandit",     "banditry" },
            { "bandits",    "banditry" },
            { "outlaw",     "banditry" },
            { "mercenary",  "mercenary" },
            { "clan",       "clan" },
        };

        // Multi-word phrases → concept (checked via string.Contains)
        private static readonly Dictionary<string, string> GameConceptPhrases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "at war",         "war" },
            { "make peace",     "peace" },
            { "don't trust",    "distrust" },
            { "do not trust",   "distrust" },
            { "can't trust",    "distrust" },
            { "swear fealty",   "fealty" },
            { "join kingdom",   "fealty" },
            { "take the throne","rebellion" },
            { "behind your back","betrayal" },
            { "in love",        "romance" },
            { "spy network",    "espionage" },
        };

        // ================================================================
        // EXTRACTIVE SUMMARY (Phase 1 fallback)
        // ================================================================

        private static void RegenerateExtractSummary(NpcMemoryFile mem)
        {
            // Simple extractive summary: take the most keyword-dense messages
            var scored = new List<KeyValuePair<float, string>>();

            foreach (var entry in mem.ConversationLog)
            {
                var keywords = ExtractKeywords(entry.Text);
                float density = keywords.Count > 0
                    ? (float)keywords.Count / entry.Text.Split(' ').Length
                    : 0;
                scored.Add(new KeyValuePair<float, string>(density, entry.Text));
            }

            scored.Sort((a, b) => b.Key.CompareTo(a.Key));

            var sb = new StringBuilder();
            int topN = Math.Min(5, scored.Count);
            for (int i = 0; i < topN; i++)
            {
                if (scored[i].Value.Length > 100)
                    sb.AppendLine(scored[i].Value.Substring(0, 100) + "...");
                else
                    sb.AppendLine(scored[i].Value);
            }

            mem.Summary = new MemorySummary
            {
                Text = sb.ToString().Trim(),
                UpdatedAt = DateTime.UtcNow.ToString("o"),
                CoversMessages = mem.ConversationLog.Count
            };
        }

        // ================================================================
        // FILE I/O
        // ================================================================

        private static NpcMemoryFile LoadOrCreate(string npcId, string npcName)
        {
            return _cache.GetOrAdd(npcId, id => 
            {
                string filePath = GetFilePath(id);
                NpcMemoryFile mem;

                if (File.Exists(filePath))
                {
                    try
                    {
                        string json = File.ReadAllText(filePath, Encoding.UTF8);
                        mem = JsonConvert.DeserializeObject<NpcMemoryFile>(json);
                    }
                    catch (Exception ex)
                    {
                        LothbrokSubModule.Log("ERROR loading memory for " + id + ": " + ex.Message,
                            TaleWorlds.Library.Debug.DebugColor.Red);
                        mem = new NpcMemoryFile { NpcId = id, NpcName = npcName };
                    }
                }
                else
                {
                    mem = new NpcMemoryFile
                    {
                        NpcId = id,
                        NpcName = npcName,
                        Metadata = new NpcMetadata
                        {
                            FirstMet = DateTime.UtcNow.ToString("o")
                        }
                    };
                }

                return mem;
            });
        }

        private static void Save(NpcMemoryFile mem)
        {
            try
            {
                string filePath = GetFilePath(mem.NpcId);
                lock (mem)
                {
                    string json = JsonConvert.SerializeObject(mem, Formatting.Indented);
                    File.WriteAllText(filePath, json, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                LothbrokSubModule.Log("ERROR saving memory for " + mem.NpcId + ": " + ex.Message,
                    TaleWorlds.Library.Debug.DebugColor.Red);
            }
        }

        private static string GetFilePath(string npcId)
        {
            // Sanitize NPC ID for filename
            string safe = npcId.Replace("\\", "_").Replace("/", "_")
                .Replace(":", "_").Replace("*", "_").Replace("?", "_")
                .Replace("\"", "_").Replace("<", "_").Replace(">", "_")
                .Replace("|", "_");
            return Path.Combine(_saveDataDir, safe + ".lothbrok.json");
        }

        /// <summary>
        /// Flush all cached memory files to disk.
        /// Called on campaign save and mod unload.
        /// </summary>
        public static void FlushAll()
        {
            foreach (var mem in _cache.Values)
            {
                Save(mem);
            }
            LothbrokSubModule.Log("Flushed " + _cache.Count + " NPC memory files.");
        }

        /// <summary>
        /// Clear the in-memory cache. Called on campaign end.
        /// </summary>
        public static void ClearCache()
        {
            FlushAll();
            _cache.Clear();
        }
    }
}
