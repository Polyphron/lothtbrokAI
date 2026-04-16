# LothbrokAI: Game Master Navigator CLI

The **Game Master Navigator** is a standalone Python application that acts as your sentient co-pilot while playing Mount & Blade: Bannerlord. 

Instead of waiting for off-screen agent loops to tick, you leave this CLI open on a second monitor. It connects directly to the living Bannerlord engine (via the local REST API) and allows you to chat with a large language model (LLM) about live geopolitical strategies, clan relationships, and systemic weaknesses.

---

## 1. Setup & Installation

The CLI requires Python 3.10+ and runs completely out-of-band from the Unity/TaleWorlds engine.

```bash
cd cli/
pip install -r requirements.txt
```

### Environment Configuration
Copy `.env.example` to `.env`:
```bash
copy .env.example .env
```

Open `.env` and configure your LLM provider. The CLI supports both blazing fast local models (via LMStudio) or intelligence-heavy cloud models (via OpenRouter).

**For OpenRouter (Recommended for deep strategic reasoning):**
```env
OPENROUTER_API_KEY=sk-or-v1-abcdef...
MODEL_NAME=meta-llama/llama-3-8b-instruct:free
USE_LOCAL_MODEL=false
```

**For LMStudio (Local Execution):**
```env
USE_LOCAL_MODEL=true
LMSTUDIO_URL=http://localhost:1234/v1
```

---

## 2. Pinging the Bannerlord Local API

The LothbrokAI C# mod spins up a `LocalGameMasterServer` immediately upon entering the campaign map.
The server runs on: `http://localhost:8080/`

**Endpoints used by this CLI:**
*   `GET /api/graph`: Returns the entire living node/edge topology of Calradia (Heroes, Clans, Kingdoms, Settlements).

---

## 3. How to Use

1. **Boot Bannerlord:** Load your campaign. Wait for the green console message: `Game Master REST API started on port 8080`.
2. **Launch CLI:** 
   ```bash
   python navigator.py
   ```
3. **Chatting:**
   The LLM does not load the entire 3000+ node graph into its prompt (to save tokens). Instead, it uses **Omni-Tools** (`query_entity`, `get_relations`, `find_weakest_vassal`). 

   **Example Prompts:**
   * *"I want to betray Vlandia. Who is the weakest clan in the kingdom?"*
   * *"I've captured epic loot. Which neighboring Kingdom is the richest to sell to right now?"*
   * *"Who has severely negative relations with Caladog? I need someone who hates him."*

The LLM will automatically call the Python crawler tools, fetch the live game data mapped from `networkx`, and respond with tactical advice.

---

## 4. Architecture Notes

* **Stateless Client:** The CLI is stateless. Every query pulls the absolute latest Graph node states from the C# loop. If a Lord dies in battle at 12:00PM, and you ask about him at 12:01PM, the CLI will see he is gone.
* **Extensibility:** To add new tools, simply write a standard Python function returning JSON data in `navigator.py`, and register it in the OpenAI `tools` list array.
