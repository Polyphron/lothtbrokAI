# AIInfluence Changelog Gap Analysis
*Features and fixes that reveal scope gaps in LothbrokAI*

## NEW FEATURES WE MISSED

### Critical (must have)
| Feature | Source | Why Critical |
|---------|--------|-------------|
| **Troop/prisoner transfer via dialogue** | v3.3.7 | NPCs transfer specific troops/prisoners. Real army management through conversation. |
| **Kingdom power transfer (abdication)** | v3.3.6 | Ruler can abdicate, transfer kingdom to vassal via dialogue. Dynasty mechanic. |
| **Fief transfer in dialogue** | v4.0.0 | Kingdom leaders grant fiefs, clan leaders grant own fiefs. We had this in scope but thin. |
| **Player death → character switch** | v4.0.0 fix | When player dies, continue as clan member. AI MUST update all NPC contexts. Dynasty critical. |
| **RP Item creation + persistence** | v3.3.6, v3.3.7 | NPCs create letters/documents with names/descriptions. Items persist, always visible in AI prompt. |
| **Internal thought process (CoT prompting)** | v3.3.7 | Multi-step reasoning: fact check → lie detect → context verify → character consistency → coherence. This is WHY the AI feels smart. |
| **Hostile encounter resolution** | v4.0.3 fix | If NPC agrees to peace in dialogue, don't force combat. Critical bug guard. |

### Important (should have)
| Feature | Source | Notes |
|---------|--------|-------|
| **Kingdom capitals** | v4.0.0 | Auto-determined from most prosperous ruling clan town. Referenced in diplomacy. |
| **Notable role recognition** | v3.3.6 | Headmen, landowners, artisans, merchants, gang leaders, preachers. Affects prompts. |
| **Army reinforcement awareness** | v4.0.0 | NPCs know who's marching to join army. Better strength assessment. |
| **Settlement inventory awareness** | v3.3.7 | NPCs in settlements see settlement inventory, not their own. |
| **NPC economic awareness** | v4.0.0 | NPCs know about drought, crop failure, economic events. |
| **Settlement prosperity context** | v4.0.3 fix | Town vs castle vs village wealth evaluated correctly. |
| **Custom action rules** | v3.3.7 | actionrules.txt — user-editable rules for action validation. |
| **Prompt caching** | v3.3.7 | Thought process at beginning of prompt for API-level caching. |

### Nice to have (skip for MVP)
| Feature | Source | Notes |
|---------|--------|-------|
| Arena training | v4.0.2 | Cool but not AI-dialogue related |
| Summon troops (Ctrl+Alt+X) | v4.0.0 | Tactical feature |
| Spawning validation (navmesh) | v4.0.0 fix | Quality of life |
| Combat phrase positioning | v4.0.0 fix | UI detail |

## BUG GUARDS WE MUST IMPLEMENT

These are bugs the old mod found and fixed. We'll have the same bugs if we don't guard against them:

| Bug | Fix | Risk |
|-----|-----|------|
| Map orders during conversation crash | Don't send patrol/go-to while in mission scene | HIGH |
| Quest targeting non-existent NPCs | Validate NPC exists before creating quest | HIGH |
| Surrender → duplicate hero on loot screen | Proper prisoner handling | MEDIUM |
| Army combat target wrong | Attack the lord you clicked, not army leader | MEDIUM |
| Character death → AI treats you as dead hero | Update ALL NPC contexts on character switch | HIGH |
| Marriage childbirth crash without father | Already have this patch | ✅ DONE |
| Diplomacy rounds hang on player non-response | Player is optional participant | MEDIUM |
| AI demands unreachable settlements | Geography-aware territory demands | MEDIUM |
| AI fabricates ports for inland towns | Only mark real ports (War Sails) | LOW |
| Diplomatic proposals stuck in PENDING | Need reject action, not just accept | MEDIUM |
| NPC initiative during army march | Don't interrupt if lord is in army | LOW |

## ARCHITECTURAL INSIGHTS

### Chain-of-Thought Prompting (the hidden sauce)
The old mod's v3.3.7 "thought process" is basically a structured CoT:
1. Step 0: Fact source identification (what does AI actually know from game data?)
2. Step 3: Lie detection (does player claim match game state?)
3. Step 4: Conversation context continuity (is this consistent with history?)
4. Step 7: Character consistency (accent, speech, personality alignment)
5. Step 9: Final coherence check (does everything make sense?)

This is CRITICAL. Without this, the AI just generates text. With this, it reasons about game state. Our ActionEngine + PromptBuilder MUST include this.

### Prompt Caching
Moving static content (thought process template, world description) to the beginning of the prompt lets API providers (OpenRouter, etc.) cache the prefix. Saves tokens and money on repeated calls. Our PromptBuilder should use this pattern:
```
[STATIC: System prompt + Thought process template + World description]  ← cached
[SEMI-STATIC: NPC personality + relationship state]  ← changes rarely  
[DYNAMIC: Memory payload + recent messages + current context]  ← changes every turn
```
