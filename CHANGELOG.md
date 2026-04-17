# Changelog

All notable changes to LothbrokAI will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.2] - 2026-04-17

### Added
- **Take Gold Action**: Added `TakeGoldHandler` to mechanically support players bribing/gifting gold to NPCs (LLM can extract gold if offered).

### Changed
- **System Prompt Formatting**: Stopped prompting the LLM with OpenAI function-calling parameters and reverted strictly to the explicit custom `[ACTIONS]` JSON text block injection to match `ResponseProcessor.cs` parser.
- **Action Engine Resilience**: Handlers now gracefully fallback to parsing JSON `"value"` keys instead of strictly typing on implicit strings (like `"gold"`).

### Fixed
- **Dialogue History Clamping**: Clicking "Type a response" no longer instantly overwrites the NPC response with "...". Added a `lothbrok_chat_waiting_state` node that cleanly preserves {GENERATED_NPC_TEXT} while the player is typing their message, transitioning to the background "Check for response..." phase.
- **Vanilla Dialogue Looping**: The "Return to normal matters" generic exit option is now safely mapped to the `lord_pretalk` node (Vanilla Root Hook) to prevent the AI menu from entering an infinite deadlock.

## [0.4.1] - 2026-04-16

### Fixed
- **MemoryEngine Concurrent Corruption**: Refactored `_cache` from `Dictionary` to `ConcurrentDictionary` and implemented JSON serialization `Mutex(mem)` locking. This solves the catastrophic silent memory array corruptions caused by the `Task.Run` async API generating dialogue simultaneously alongside Bannerlord's Daily Tick firing `OnBeforeSave()`. All Memory memory flushes are now completely thread-safe.

## [0.4.0] - 2026-04-16

### Added
- **Standalone Diplomacy Pipeline**: Stripped the massive `Bannerlord.Diplomacy` Mod dependency out of the project. LothbrokAI is now fully independent.
- **Native Alliance Storage**: Alliances and Non-Aggression Pacts functionally map natively into the `.sav` via `MediciState`.
- **The Alliance Cascade System**: Authored `LothbrokDiplomacyBehavior`. Native listening hook ensuring that allied Kingdoms actively join defensive and offensive wars declared through LLM conversation automatically. 

### Changed
- `ActionEngine` completely intercepts `propose_alliance` and `break_alliance` and writes directly to `MediciState`.
- `ContextAssembler` dynamically translates `MediciState` bi-directional hashes for the prompt string.

## [0.3.1] - 2026-04-16

### Added
- **`execute_game_action` Omni-Tool**: LLM now natively triggers thread-safe game mutations (`declare_war`, `transfer_fief`, `grant_favor`, etc.) through OpenAI's function calling block instead of the deprecated, parsing-heavy `[ACTIONS]` dictionary.
- **Dynamic Context Action Injection**: Added Rank-sensitive System Prompts. Kingdom Rulers possess the implicit capability to see and run Diplomatic tools (`propose_peace`, `declare_war`), whereas Wandering Heroes only see immediate relationship actions (`relation_change`, `spread_rumor`).
- **Main Thread Execution Sandbox**: Added `ActionEngine.ProcessQueuedActions` directly to Bannerlord's `OnApplicationTick` hook, ensuring complex diplomatic mutations execute correctly without corrupting the save file. 

## [0.3.0] - 2026-04-16

### Added
- **Calradia Graph Exporter**: Transforms Unity/TaleWorlds game objects (Heroes, Clans, Settlements) into a `networkx`-compatible JSON graph topology based on Aphygo ckglib principles.
- **Local Game Master Server (`LocalGameMasterServer.cs`)**: A non-blocking thread-safe `HttpListener` running natively on `http://localhost:8080/`. Streams live Calradia graph data without file I/O overhead.
- **Concurrent REST API Queues**: Implemented `POST /api/omni-tool` endpoint with thread-safe `ConcurrentQueue` ingestion to safely trigger logic on the Unity Main Thread loop (`OnApplicationTick`).
- **Python Map Navigator CLI (`cli/navigator.py`)**: A standalone LLM REPL interacting with the living Bannerlord API. Uses Tool-Calling parameters to filter and traverse massive `networkx` graphs seamlessly for live strategic gameplay advice.

## [0.2.0] - 2026-04-16

### Added
- **The Medici Engine:** Full political intrigue framework supporting native ActionEngine validations for Favors, Reputations, and Blackmail/Leverage.
- **SaveBehavior:** Seamlessly integrates LothbrokAI political state (Social Factions, Active Quests, RPItems) directly into native Bannerlord `.sav` files.
- **Quest & Letters System:** Generates physical roleplay evidence (ledgers, rumors, letters) and tracks dynamic game-mechanic targets natively.
- **Deep Layer Context:** Injects immediate relationship graphs (native Friends and Enemies) into the prompt string without blocking character reasoning.
- **Omni-Tool Protocol (Agent Pipeline):** Major refactor of `APIRouter.cs` to support Multi-Turn async function calling. Added `ToolRegistry.cs` collapsing 50+ lookups into 3 Omni-Tools (`query_character`, `query_settlement`, `query_kingdom`) to eliminate token bloat.

### Removed
- **SpyNetwork Prototype:** Discarded the alley-dependent spymaster mechanic in favor of native Agent execution and UI integration.

## [0.1.0] - 2026-04-16

### Added
- Project scaffold: csproj, SubModule.xml, agents.md
- Module manifest with Harmony + UIExtenderEx dependencies
- Folder structure for all planned components
- Reverse engineered 22 vanilla bug fix patches from AIInfluence v4.1.0
- Full DLL analysis documented (1,755 types, call chain mapped)

### Architecture Decisions
- Standalone mod (no AIInfluence dependency)
- .NET 4.7.2 / C# 7.3 (Bannerlord requirement)
- Per-NPC memory files with `.lothbrok.json` extension
- Token-budgeted prompt assembly (max 4K tokens per interaction)
- Async API calls off game thread
