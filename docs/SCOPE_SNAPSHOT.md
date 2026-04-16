# LothbrokAI — Complete Scope Snapshot
*Captured: 2026-04-16 02:10 CET*
*Status: Scope locked. Phase 1 in progress.*

---

## What Is LothbrokAI?

A standalone Bannerlord AI companion mod that replaces AIInfluence. Built from scratch with:
- Smart memory (token-budgeted, not 40K dumps)
- Game-mechanic-aware AI (not LLM imagination)
- Political intrigue simulation ("The Medici Engine")
- Stable save synchronization
- 80+ kingdom compatibility (Separatism)

---

## Core Design Principles

### 1. "Game Decides, AI Narrates"
Action outcomes come from Bannerlord's game mechanics (skills, relations, combat stats), not LLM judgment. The AI narrates the result but doesn't choose it. This solves model inconsistency across different LLM backends.

### 2. "Relationship Gates Everything"
Relationship is a hard gate on what actions are possible:
- **Lover** (+80 to +100): Can manipulate into almost anything
- **Trusted Ally** (+50 to +79): Most persuasion works
- **Friendly** (+20 to +49): Persuasion with skill checks
- **Neutral** (-19 to +19): Basic conversation only
- **Hostile** (-20 to -49): Need overwhelming evidence or leverage
- **Enemy** (-50 to -100): BLOCKED. Must use proxy chains through the social graph.

*"A lover can do what an army cannot."*

### 3. Token-Budgeted Prompts
3,200 tokens max (vs AIInfluence's 40,000+). Structured for API prefix caching:
- Static: System prompt + CoT template (cached)
- Semi-static: Personality + relationship (changes rarely)
- Dynamic: Memory + context + current message (per-turn)

### 4. Chain-of-Thought Reasoning (from RE of AIInfluence v3.3.7)
5-step internal reasoning before every response:
1. Fact verification (what does AI actually know?)
2. Lie detection (player claims vs game state)
3. Conversation context continuity
4. Character consistency (accent, speech, personality)
5. Final coherence check

---

## Architecture

### Save Synchronization (Hybrid)
- **Layer 1 (SyncData):** Trust, romance, reputation, leverage, quests, conspiracies → inside Bannerlord's .sav file. Guaranteed rollback on reload.
- **Layer 2 (External):** Conversation history + embeddings → .lothbrok.json files with game-day watermarks. Future entries pruned on reload.

### Memory Architecture
- Phase 1: TF-IDF keyword retrieval (ported from hotfix)
- Phase 2: Remote embeddings via LM Studio (network) or OpenRouter API
- Tagged Memory Graph: 1-hop spreading via shared context tags
- Per-NPC JSON files: `{npcId}.lothbrok.json`

### API Layer
- Backends: OpenRouter, DeepSeek, Ollama, LM Studio, KoboldCpp
- All use OpenAI-compatible `/v1/chat/completions` format
- Embeddings via LM Studio (network) or OpenRouter — zero local compute
- Sync now, async in Phase 5

### Dependencies (minimal)
- Bannerlord.Harmony (2.4.2+)
- Bannerlord.UIExtenderEx (2.11+)
- Newtonsoft.Json (bundled with game)
- LM Studio on network (embedding + chat)
- OpenRouter API (fallback)

---

## Complete System Inventory (49 source files)

### Core (5 files)
- DialogueInterceptor.cs — Harmony hooks on vanilla conversation
- PromptBuilder.cs ✅ — Token-budgeted prompt assembly with CoT
- ContextAssembler.cs — NPC identity + location + world context
- ResponseProcessor.cs — Parse AI response, extract actions
- ActionEngine.cs — "Game decides, AI narrates" action resolution

### Memory (8 files)
- MemoryEngine.cs ✅ — Retrieve + store + summarize
- TaggedMemoryGraph.cs — Context-linked memory with tag spreading
- KeywordRetriever.cs — TF-IDF keyword-based retrieval
- VectorStore.cs — Remote embedding retrieval (Phase 2)
- SummaryManager.cs — Rolling conversation summaries
- EmotionalState.cs — NPC emotional state machine
- PersonalityGenerator.cs — First-contact personality via LLM
- ConversationLog.cs — Append-only full history

### World (4 files)
- WorldContext.cs — Filtered world state for prompts
- KingdomTracker.cs — Kingdom/faction state (80+ kingdoms)
- EventManager.cs — Dynamic events (generation + spreading + evolution)
- NewsSystem.cs — In-game news notifications

### API (4 files)
- APIRouter.cs ✅ — Backend routing
- OpenRouterClient.cs — OpenRouter API (merged into APIRouter)
- OllamaClient.cs — Ollama / LM Studio (merged into APIRouter)
- TokenEstimator.cs ✅ — Prompt token counting

### Systems (7 files)
- RomanceSystem.cs — Romance progression + NPC-initiated
- MarriageSystem.cs — Marriage + Norse polygamy
- DiplomacySystem.cs — Kingdom diplomacy (Separatism compatible)
- AIActionSystem.cs — NPC follow/patrol/tasks
- MissionSystem.cs — Send companions on missions
- MessengerSystem.cs — Send letters (gold cost, interception)
- DuelSystem.cs — One-on-one duels

### Intrigue — "The Medici Engine" (9 files)
- QuestEngine.cs — Game-mechanic-aware AI quest generation
- QuestValidator.cs — Validates quests against real game state
- SpyNetwork.cs — Companion spy network via alley system
- LeverageSystem.cs — Political leverage resource (with decay)
- LetterSystem.cs — AI-generated quest items (letters, evidence)
- SocialInfluence.cs — NPC-to-NPC opinion propagation
- FactionDynamics.cs — Emergent faction detection within kingdoms
- ConspiracyEngine.cs — Multi-step plots (overthrow, counter-intel)
- ReputationSystem.cs — Three-axis reputation (Honor/Fear/Influence)

### Patches (3 files)
- VanillaBugFixes.cs — 22 ported from AIInfluence
- ConversationPatches.cs — Dialogue system hooks
- CampaignPatches.cs — Campaign event hooks

### Data (4 files)
- NpcDataStore.cs — Per-NPC data management
- SaveManager.cs — Save/load lifecycle + watermark sync
- DataMigrator.cs — Import from AIInfluence saves
- ConfigManager.cs ✅ — MCM settings (as LothbrokConfig.cs)

### Entry Point
- LothbrokSubModule.cs ✅ — Wired up, building clean

---

## Features from AIInfluence (parity)

### Included (reverse-engineered)
- Dynamic AI dialogues with every NPC
- NPC personality/backstory/speech quirk generation
- Memory system (conversation history, summaries)
- Trust system affecting NPC openness
- Lie detection (checking player claims vs game state)
- Conflict escalation (dialogue → combat)
- World events awareness + event spreading/evolution
- Naval DLC / War Sails support
- Workshop/item trading via dialogue
- Multiple AI backends
- AI Diplomacy (alliances, trade agreements, territory transfers, tribute, reparations, war fatigue, clan expulsion, diplomacy rounds)
- Romance + NPC-initiated romance + relationship degradation
- Marriage system (extended with Norse polygamy)
- Settlement combat (3 initiation methods, defender analysis, companion decisions, lord intervention, civilian panic, village actions)
- Death history / AI obituary
- NPC commands (follow, go-to, patrol, garrison, wait, return, create party, attack, siege, raid)
- Action persistence on save/load
- NPC initiative conversations
- RP item creation + persistence
- Troop/prisoner transfer via dialogue
- Kingdom power transfer (abdication)
- Fief transfer in dialogue
- Player death → character switch
- Kingdom capitals
- Notable role recognition
- Settlement inventory awareness
- Companion mission system
- Messenger system (gold cost, interception)
- Group conversations

### Excluded (by design)
- Disease system (bloat, annoying gameplay)
- Battle tactics AI (out of scope)
- Text-to-Speech (TTS)
- Arena training (not AI-related)

---

## LothbrokAI Exclusive Features (not in AIInfluence)

- **ActionEngine:** "Game decides, AI narrates" — skill-gated action resolution
- **Tagged Memory Graph:** Context spreading via shared tags
- **The Medici Engine:**
  - Social Influence Propagation (NPC-to-NPC opinion spreading)
  - Emergent Faction Dynamics (Loyalists, Hawks, Doves, Conspirators)
  - Conspiracy Engine (multi-step plots with discovery risk)
  - Three-Axis Reputation (Honor/Fear/Influence)
  - Patronage Networks (favors as currency)
  - Succession Crises (political event chains on ruler death)
  - Economic Warfare (fund armies, bankrupt rivals, embargos)
- **Proxy Manipulation:** Enemies BLOCKED — must work through social graph
- **Norse Polygamy:** Multiple marriages through negotiation
- **Save Synchronization:** Hybrid SyncData + watermark pruning
- **Token-Budgeted Prompts:** 3,200 tokens vs 40,000+
- **Prompt Caching Layout:** Static → semi-static → dynamic ordering

---

## Phase Plan

| Phase | Hours | Days | Focus |
|-------|-------|------|-------|
| 1: Dialogue MVP | 16h | 2 | Core pipeline, API, personality, bug fixes |
| 2: Memory + Social | 16h | 2 | Tagged memory, emotions, romance, marriage |
| 3: World + Diplomacy + Commands | 31h | 3 | 80+ kingdom diplomacy, events, NPC orders |
| 4: Combat + Quests + Intrigue | 38h | 3 | ActionEngine, Medici Engine, settlement combat |
| 5: Polish + Parity | 12h | 2 | MCM, migration, async, testing |
| **TOTAL** | **~113h** | **~12 days** | |

---

## Build Status

| Component | Status |
|-----------|--------|
| LothbrokSubModule.cs | ✅ Building |
| LothbrokConfig.cs | ✅ Building |
| APIRouter.cs | ✅ Building |
| TokenEstimator.cs | ✅ Building |
| PromptBuilder.cs | ✅ Building |
| MemoryEngine.cs | ✅ Building |
| **Full project** | **✅ 0 errors, 0 warnings** |

---

## Known Bug Guards (from AIInfluence changelogs)

1. Governor follows player → town loses governor → ship selling crash
2. Map orders during conversation → crash
3. Quest targeting non-existent NPCs
4. Surrender → duplicate hero on loot screen
5. Army combat → wrong target (army leader instead of lord)
6. Player death → AI treats you as dead hero
7. Marriage childbirth crash without father (already patched)
8. Diplomacy rounds hang on player non-response
9. AI demands unreachable settlements
10. AI fabricates ports for inland towns
11. Diplomatic proposals stuck in PENDING (need reject action)

---

## Project Location
`C:\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\LothbrokAI`

## Key Documents
- `agents.md` — Project structure and conventions
- `implementation_plan.md` — Full technical design (artifact)
- `CHANGELOG.md` — Version history

---

*"A lover can do what an army cannot."*
*We build the brain first. The body follows.* 🐺⚔️
