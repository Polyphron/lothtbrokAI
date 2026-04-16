# LothbrokAI — Agents Guide

## Overview
Standalone Bannerlord AI companion mod replacing AIInfluence. Provides AI-powered NPC
conversations with semantic memory, smart context assembly, and proper data lifecycle.

## Architecture

```
LothbrokAI/
├── SubModule.xml                    # Bannerlord module manifest
├── LothbrokAI.csproj               # .NET 4.7.2, C# 7.3
├── agents.md                       # This file
├── CHANGELOG.md                    # Version history
│
├── src/
│   ├── LothbrokSubModule.cs        # Entry point (MBSubModuleBase)
│   │
│   ├── Core/                       # Dialogue pipeline
│   │   ├── DialogueInterceptor.cs  # Harmony hooks on vanilla conversation system
│   │   ├── PromptBuilder.cs        # Token-budgeted prompt assembly
│   │   ├── ContextAssembler.cs     # Combines memory + world into prompt payload
│   │   ├── ResponseProcessor.cs    # Parse AI response, extract actions
│   │   ├── ActionEngine.cs         # "Game decides, AI narrates" action resolution
│   │   └── ToolRegistry.cs         # Omni-Tool Protocol for Agent async function calls
│   │
│   ├── Memory/                     # NPC memory system
│   │   ├── MemoryEngine.cs         # Orchestrator: retrieve + store + summarize
│   │   ├── TaggedMemoryGraph.cs    # Context-linked memory with tag spreading
│   │   ├── KeywordRetriever.cs     # TF-IDF keyword-based retrieval (Phase 1)
│   │   ├── VectorStore.cs          # ONNX embedding retrieval (Phase 2)
│   │   ├── SummaryManager.cs       # Rolling conversation summaries
│   │   ├── EmotionalState.cs       # NPC emotional state machine
│   │   ├── PersonalityGenerator.cs # First-contact NPC personality via LLM
│   │   └── ConversationLog.cs      # Append-only full history
│   │
│   ├── World/                      # World state management
│   │   ├── WorldContext.cs         # Filtered world state for prompts
│   │   ├── KingdomTracker.cs       # Kingdom/faction state (designed for 80+ kingdoms)
│   │   ├── EventManager.cs         # Dynamic world events (feudal history flavor)
│   │   └── NewsSystem.cs           # In-game news notifications for AI events
│   │
│   ├── API/                        # LLM backend integration
│   │   ├── APIRouter.cs            # Backend selection and routing
│   │   ├── OpenRouterClient.cs     # OpenRouter API
│   │   ├── OllamaClient.cs         # Ollama (local)
│   │   ├── TokenEstimator.cs       # Prompt token counting
│   │   └── LocalGameMasterServer.cs# Port 8080 HttpListener for Live API access
│   │
│   ├── Systems/                    # Gameplay systems
│   │   ├── RomanceSystem.cs        # Romance progression
│   │   ├── MarriageSystem.cs       # Marriage + Norse polygamy
│   │   ├── DiplomacySystem.cs      # Kingdom diplomacy (designed for Separatism)
│   │   ├── AIActionSystem.cs       # NPC follow/patrol/tasks
│   │   ├── MissionSystem.cs        # Send companions on missions
│   │   ├── MessengerSystem.cs      # Send letters (costs gold, can be intercepted)
│   │   └── DuelSystem.cs           # One-on-one duels
│   │
│   ├── Intrigue/                   # "The Medici Engine" — political intrigue system
│   │   ├── QuestEngine.cs          # Game-mechanic-aware AI quest generation
│   │   ├── QuestValidator.cs       # Validates quests against real game state
│   │   ├── SpyNetwork.cs           # Companion spy network via alley system
│   │   ├── LeverageSystem.cs       # Political leverage resource (with decay)
│   │   ├── LetterSystem.cs         # AI-generated quest items (letters, evidence)
│   │   ├── SocialInfluence.cs      # NPC-to-NPC opinion propagation
│   │   ├── FactionDynamics.cs      # Emergent faction detection within kingdoms
│   │   ├── ConspiracyEngine.cs     # Multi-step plots (overthrow, counter-intel)
│   │   └── ReputationSystem.cs     # Three-axis reputation (Honor/Fear/Influence)
│   │
│   ├── Patches/                    # Harmony patches on vanilla Bannerlord
│   │   ├── VanillaBugFixes.cs      # Ported from AIInfluence's proven fixes
│   │   ├── ConversationPatches.cs  # Dialogue system hooks
│   │   └── CampaignPatches.cs      # Campaign event hooks
│   │
│   └── Data/                       # Serialization and persistence
│       ├── NpcDataStore.cs         # Per-NPC data management
│       ├── SaveManager.cs          # Save/load lifecycle
│       ├── DataMigrator.cs         # Import from AIInfluence saves (testing)
│       └── ConfigManager.cs        # MCM settings integration
│
├── data/                           # User-editable configuration
│   ├── world.txt                   # World description for AI
│   ├── prompts/                    # Prompt templates (hot-reloadable)
│   │   ├── system.txt              # System prompt template
│   │   ├── personality.txt         # NPC personality template
│   │   └── context.txt             # World context template
│   └── config.json                 # Mod settings
│
├── cli/                            # Standalone Application Layer
│   ├── navigator.py                # Python LLM Map Navigator
│   ├── requirements.txt            # Python dependencies (networkx, openai)
│   └── README.md                   # Setup guide and architecture overview
│
├── save_data/                      # Per-campaign save data
│   └── {campaign_id}/
│       ├── {npc_id}.lothbrok.json  # Per-NPC memory files
│       └── _world_cache.json       # Cached world state
│
└── logs/                           # Runtime logs
    ├── lothbrok.log                # Main mod log
    └── prompts/                    # Full prompt logs (debug)
```

## Tech Stack
- **Framework:** .NET 4.7.2, C# 7.3 (Bannerlord requirement)
- **Patching:** Harmony 2.4.2+ via Bannerlord.Harmony
- **UI:** Bannerlord.UIExtenderEx 2.11+
- **JSON:** Newtonsoft.Json (bundled with game)
- **Embeddings (Phase 2):** ONNX Runtime + all-MiniLM-L6-v2

## How to Build
```bash
cd C:\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\LothbrokAI
dotnet build LothbrokAI.csproj -c Release
```
DLL outputs to `bin\Win64_Shipping_Client\LothbrokAI.dll`

## How to Run
1. Build the project
2. Enable "LothbrokAI" in the Bannerlord launcher
3. Load a campaign
4. Talk to any NPC

## Key Design Principles
1. **Token budget first** — Every prompt component has a max allocation. Total never exceeds 4K tokens.
2. **Memory, not history** — Retrieve relevant past conversations, don't dump everything.
3. **Async API calls** — LLM requests happen off the game thread. Never block the main loop.
4. **Data hygiene** — Every data structure has a lifecycle. Nothing grows unboundedly.
5. **Fail loud in dev** — Full stack traces, no swallowed exceptions. Graceful degradation only in release.
6. **Hot-reloadable config** — Prompt templates and world description reload without game restart.

## Conventions
- All Harmony patches go in `src/Patches/` with descriptive class names
- Every patch class documents WHAT it patches, WHY, and the VANILLA BUG it fixes
- Log at INFO level for events, DEBUG for state, ERROR for failures
- NPC data files use `.lothbrok.json` extension to avoid conflicts with AIInfluence
- All public methods have XML doc comments explaining purpose and non-obvious behavior
- **Reverse-Engineer First**: When encountering Bannerlord engine quirks (e.g., missing UniqueGameId strings or native diplomacy logic), always `grep` and inspect legacy working mods (like `AIInfluence`) before inventing novel solutions. Do not reinvent the wheel for solved engine idiosyncrasies.

## Common Tasks

### Add a new Harmony patch
1. Add patch class to `src/Patches/`
2. Document the vanilla method being patched and why
3. Register in `LothbrokSubModule.OnSubModuleLoad()`
4. Test with and without the patch

### Add a new API backend
1. Create client class in `src/API/`
2. Implement same interface as other clients
3. Register in `APIRouter`
4. Add configuration to `config.json`

### Debug a conversation
1. Check `logs/lothbrok.log` for the conversation flow
2. Check `logs/prompts/` for the full prompt sent to the LLM
3. Check `save_data/{campaign}/` for the NPC's memory state

## Reverse Engineering Notes
This mod reverse-engineered 22 vanilla bug fix patches from AIInfluence v4.1.0.
Key patches ported:
- `MarriageRomanceProtectionPatch` — prevents pregnancy without husband crash
- `HeroCreationNullEquipmentRosterPatch` — null equipment on hero creation
- `PreventClanDestructionPatch` — prevents player clan from being destroyed
- `PreventCompanionKillOnBadRelationPatch` — companions killed on bad relation
- `PartySpeedExplainedBugFixPatch` — party speed calculation crash
- `PlayerDeathPatch` — player death handling
- `EditableTextWidgetMaxLengthPatch` — text input length limit

## Future Scope (Phase 8 - The Agentic Simulation)
- **Dual-Model Configuration**: Support lightweight models (e.g., Llama-3-8B/Haiku) for cheap background simulation tasks, reserving heavy models (Sonnet 3.5/GPT-4o) exclusively for player dialogue.
- **Off-Screen Agent Loops**: Allow Lords to periodically invoke the lightweight LLM to negotiate alliances or betrayals completely off-screen, shifting the political landscape organically over time.
- **Generative Casus Belli**: Faction leaders evaluate neighboring strength via Omni-Tools to generate custom, context-aware narrative reasons for declaring war.
- **Live Game Master CLI**: Expose the Omni-Tool REST API to localhost. This allows out-of-game agents (like Antigravity MCP skills) to hook directly into the live engine, spawning armies, generating dynamic quests, and altering the living world dynamically. Out-of-game chatting logic (e.g., Discord bots) is explicitly rejected to maintain immersion.
