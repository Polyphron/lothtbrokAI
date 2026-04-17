# Bannerlord Native Mechanics Reference
> **Purpose:** Prevent LothbrokAI from reinventing mechanics the engine already handles.
> **Rule:** If Bannerlord does it, we nudge it. We never replace it.
>
> Last researched: April 2026 (v1.2.x + War Sails DLC)

---

## 1. SETTLEMENT ECONOMICS

### 1.1 The Four Pillars (all have native daily tick simulation)

| Attribute | What Drives It | What It Affects | Modifiable? |
|-----------|---------------|-----------------|-------------|
| **Prosperity** | Food surplus, security, loyalty, building projects, trade activity | Tax income, troop quality, market goods quality, recruitment | ✅ `Town.Prosperity +=` |
| **Loyalty** | Governor culture match, policies, food availability, security, notable support | Rebellion risk (< 25 triggers), construction speed, tax bonus | ✅ `Town.Loyalty +=` |
| **Security** | Garrison strength, nearby bandit presence, resolved quests | Corruption/tax penalty, crime rate, loyalty boost | ✅ `Town.Security +=` |
| **Food** | Village production (hearth-based), garrison consumption, season, raids | Prosperity growth, starvation penalty, militia generation | ✅ `Town.FoodStocks +=` |

**Key insight:** These four attributes form a **feedback loop**. Bannerlord recalculates them every daily tick. If we nudge one (e.g., +1.0 Prosperity), the engine cascades that into tax income, troop quality, and market quality automatically.

### 1.2 Village Economics
- **Hearth** = village population. Grows naturally, determines food production output.
- Each village has a **primary production type** (Grain, Fish, Iron, Hides, etc.)
- **Bound to a town/castle** — village production feeds the parent settlement
- **Raiding** destroys hearths and halts production temporarily
- ✅ `Village.Hearth` is writable

### 1.3 Workshops
- Automated businesses: buy raw materials → produce finished goods → sell on market
- Profitability driven by **local raw material availability** and **market demand**
- Multiple workshops of same type in one town **saturate** and kill profits
- **Player can own workshops** (capped per clan tier)
- Engine handles all production/pricing internally

### 1.4 Warehouses (Added post-1.0)
- Player can stockpile raw materials and supply own workshops directly
- Creates manual supply chain management gameplay
- Engine handles storage/retrieval

### 1.5 Caravans
- Autonomous NPC parties that trade between towns for passive income
- Led by a companion; **Trade skill** of leader affects profitability
- Frequency and routes determined by **market price differentials** (engine AI)
- Can be attacked by bandits/enemies
- **Merchant notables** in towns spawn their own NPC caravans

### 1.6 Trade Price Calculations
- Prices driven internally by `DefaultTradeItemPriceFactorModel`
- Based on: local supply, demand, prosperity, nearby village production, caravan activity
- **Seasonal impact**: village production varies by season (less in winter)
- **War impact**: sieges/raids cut supply chains, causing price spikes
- ⚠️ **DO NOT Harmony-patch price models** — fragile, breaks on updates

---

## 2. NOTABLES (The Underworld Economy)

### 2.1 Types and Where They Live

| Type | Location | Role | Power Trend |
|------|----------|------|-------------|
| **Merchant** | Towns | Controls caravans, trade economy | Gains power daily |
| **Artisan** | Towns | Skilled production, workshop-adjacent | Loses power daily |
| **Gang Leader** | Towns (common areas) | Crime, black market, intimidation | Loses power daily |
| **Headman** | Villages | Community leader, food production | Gains power daily |
| **Landowner** | Villages | Rural elite, **only source of noble troops** | Stable |

### 2.2 Notable Power System
- Each notable has a **Power rating** (visible on portrait hover)
- Power determines:
  - **Recruitment**: Higher power → more/better troops available
  - **Noble troops**: Power > 200 → can provide noble-tier recruits
  - **Influence generation**: Supporter notables generate daily clan influence
    - Regular: 0.05/day, Influential: 0.1/day, Powerful: 0.15/day

### 2.3 Notable Supporter System
- **Relations ≥ 50** with a notable → they may become a **Supporter** of your clan
- Supporters provide **passive daily influence** to your clan
- Supporters in a town boost **settlement loyalty** (rebellion prevention)
- If relation drops below 50 → daily chance of losing supporter status
- If relation hits 0 → immediate loss of support

### 2.4 Notable Interactions (Native)
- **Quests**: Notables are primary quest givers (won't give quests if at war with their faction)
- **Recruitment**: Troops recruited directly from notables
- **Caravan spawning**: Only merchant notables spawn NPC caravans
- **Relations**: Individual per-notable, affected by quests, raids, disputes

**🎯 LothbrokAI Opportunity:** Notables have deep mechanical weight but ZERO personality in vanilla. The LLM can give them voice, memory, and agency. Gang Leader corruption → Security drops → Loyalty drops → Rebellion. All through existing mechanics.

---

## 3. CLAN & KINGDOM POLITICS

### 3.1 Influence System
- Primary political currency within a kingdom
- **Earned:** Battles, tournaments, quests for nobles, notable supporters
- **Spent:** Proposing policies, voting, calling armies, diplomatic actions
- Engine tracks and enforces all influence costs

### 3.2 Policies
- Kingdom-wide modifiers proposed and voted on by clans
- Examples: boost income, boost militia, increase/decrease noble power
- Applied automatically by engine
- Each policy has supporters and opponents based on clan interests

### 3.3 Defection System
- Unhappy clans can **defect** to rival kingdoms
- Triggers: poor relations with ruler, no fiefs, financial hardship
- Engine handles defection logic; we can **influence inputs** (relations, fief grants)

### 3.4 Rebellion System
- Fires when town loyalty drops **below 25** AND militia > garrison
- Creates a new **rebel clan** as an independent faction at war with original owner
- If rebel holds for 30 days → becomes legitimate clan with diplomacy
- **Prevention**: Governor culture match, high security, pro-loyalty policies

### 3.5 Marriage & Dynasty
- Player can court and marry nobles (charm-based dialogue)
- Spouse joins clan as party member
- **Pregnancy**: chance when both spouses in same settlement
- **Children**: grow to adulthood at age 18 → become full clan members
- **Heir system**: on main character death → player takes control of heir
- **Generational play** is a core game feature

### 3.6 Diplomacy (Native)
- **War/Peace**: Engine handles via `DeclareWarAction` / `MakePeaceAction`
- **Tributes**: Losing side may pay tribute for peace
- **Alliances**: Formalized system (added post-1.0) with obligations and renewal
  - Marriage alliances, trade alliances, defensive pacts
  - Alliance calls to war with relationship consequences for declining
- **Kingdom destruction**: Kingdoms losing all fiefs are permanently destroyed

---

## 4. MILITARY & COMBAT

### 4.1 Armies
- Lords can form armies by spending influence to call other parties
- Army cohesion decays over time; needs influence to maintain
- Engine handles all army formation/dissolution logic

### 4.2 Sieges
- Full siege simulation: camp → bombardment → assault
- **Parlay** with defenders (added post-1.0)
- Engine handles siege AI, wall damage, breaching

### 4.3 Garrison & Militia
- **Garrison**: Manually placed troops; consume food
- **Militia**: Auto-generated based on settlement projects and food
- Both contribute to defense rating

### 4.4 Prisoners
- Captured heroes can be held or ransomed
- Heroes can escape or be broken free
- Engine tracks prisoner location and escape chances

### 4.5 Traits System
- Heroes have traits: **Honor, Mercy, Valor, Calculating, Generosity**
- Traits affect NPC behavior, dialogue options, and relationship modifiers
- Traits shift based on player actions (executing prisoners → -Honor, -Mercy)
- ✅ Readable via `hero.GetTraitLevel(DefaultTraits.Honor)` etc.

---

## 5. WAR SAILS DLC (November 2025)

### 5.1 Naval Combat
- Physics-based wind/water simulation
- **Boarding**: Melee combat on enemy ships
- **Ramming**: Sink ships, throw men overboard, shatter oars
- **Ranged**: Arrows/ballista tear sails (mobility cripple) or damage hull/crew
- Dynamic weather: storms and trade winds affect navigation and combat

### 5.2 Fleet Management
- **18 unique ship types** (trade vessels to war galleys)
- Ship customization: siege engines, cargo holds, sails, rams
- **Shipyards**: Towns build/stock vessels based on culture and development level
- Can capture enemy ships and figureheads

### 5.3 Naval Economy
- New trade goods and coastal trade routes
- Coastal settlements can grow rich from maritime trade
- **Naval blockades**: Cut off supply lines and reinforcements by sea
- Pirates as persistent threat to trade routes

### 5.4 The Nords
- New Viking-inspired faction with unique region (jagged peaks, frozen fjords)
- New troops: mariner units, Norse-themed equipment
- Three new nautical skills: Navigation, Tactics (naval), Stewardship (fleet)

### 5.5 Map Changes
- Rivers and coastal areas are navigable
- New naval campaign movement layer
- Fleets can strike deep into enemy territory via waterways

---

## 6. WEATHER & ENVIRONMENT

### 6.1 What the Engine Simulates
- **Seasonal cycle**: Spring → Summer → Autumn → Winter
- **Weather effects on campaign map**: Rain/Snow → -30% party speed
- **Weather effects in battle**: Reduced projectile accuracy/damage, mount performance
- **Food production**: Village output varies by season (reduced in winter)
- **Visual**: Snow cover, rain, fog, dynamic sky

---

## 7. THE ENCYCLOPEDIA

- Comprehensive in-game database of all heroes, clans, kingdoms, settlements
- Tracks **relations** between heroes
- Shows **trade rumors** (price differentials between towns)
- Records **battle history**, **marriages**, **deaths**
- Engine maintains all this data automatically

---

## 8. DESIGN PHILOSOPHY: Know the Engine, Then Decide

> **This is a modding project.** Modifying, extending, and overriding core mechanics is the
> entire point. This reference exists so we make *informed* decisions — not to restrict us.
>
> For every native mechanic, the question is:
> 1. **Use as-is** — the engine's implementation is fine, just feed it better inputs
> 2. **Extend** — add intelligence/depth the engine can't provide on its own
> 3. **Override** — the vanilla behavior is wrong for our vision, replace it via Harmony
> 4. **Ignore** — we're not touching this system at all

### 🔧 USE AS-IS (Feed Better Inputs)
These work well natively. We push smarter data into them and let the engine cascade.
- Price calculations (via Prosperity/Food/Security nudges)
- Supply/demand simulation
- Seasonal food production cycles
- Village → town food chain
- Caravan route optimization
- Siege mechanics and army cohesion
- Encyclopedia tracking
- Naval combat physics (War Sails)
- Trait shifting from actions

### 🔨 EXTEND (Add What the Engine Can't Do)
The engine provides the skeleton; we add the soul.
- **Marriage/pregnancy** → Norse polygamy, AI-driven courtship, fertility modifiers
- **Diplomacy** → LLM-negotiated treaties, non-aggression pacts, trade agreements, alliance cascades
- **Rebellion** → AI-driven conspiracies leading to rebellion, not just loyalty math
- **Defection** → Medici Engine influence on clan loyalty drift
- **Notables** → LLM personality, scheming, economic agency, Gang Leader crime networks
- **Rumors/Events** → Structured propagation through settlement graphs with narrative layer

### 🧠 BUILD (Only We Can Do This)
Systems that don't exist in the engine at all.
- **Dynamic NPC personality & memory** (Medici Engine + MemoryEngine)
- **LLM-driven dialogue** (DialogueInterceptor → APIRouter → ResponseProcessor)
- **Background event generation** (Cheap Brain — deterministic rolls on traits/relations)
- **Economic effect system** (time-limited settlement attribute modifiers with causality tracking)
- **Social graph feedback loops** (economic state changes → NPC behavior events)
- **Narrative discovery** (player asks NPCs about world state; LLM narrates from event log)
- **Cross-NPC memory** (NPCs sharing information about player actions between conversations)
- **Custom political intrigue** (favors, blackmail, leverage, secret alliances)
- **Player reputation system** (Honor/Fear/Influence axes beyond vanilla traits)

### ⚡ ENGINE LEVERS (Proven Safe Inputs)
Direct writes to engine properties that cascade through native simulation.
- `Town.Prosperity +=` (trade booms, infrastructure investment)
- `Town.Security +=` (gang leader chaos, player patrols)
- `Town.Loyalty +=` (political manipulation, cultural events)
- `Town.FoodStocks +=` (supply chain disruption, harvest festivals)
- `Village.Hearth +=` (investment, raider protection)
- `ChangeRelationAction` (NPC-to-NPC relations from background plots)
- `GiveGoldAction` (bribes, tributes, economic manipulation)
- `DeclareWarAction` / `MakePeaceAction` (LLM-negotiated diplomacy)
- `ChangeOwnerOfSettlementAction` (fief transfers from political deals)
- Notable `Power` and supporter relation thresholds
