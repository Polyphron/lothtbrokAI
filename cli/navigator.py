import os
import json
import sys
from dotenv import load_dotenv
import networkx as nx
from openai import OpenAI
from colorama import init, Fore, Style

# Initialize colorama
init(autoreset=True)

# Load config
load_dotenv()
API_KEY = os.getenv("OPENROUTER_API_KEY", "")
MODEL = os.getenv("MODEL_NAME", "meta-llama/llama-3-8b-instruct:free")
LOCAL_URL = os.getenv("LMSTUDIO_URL", "http://localhost:1234/v1")
USE_LOCAL = os.getenv("USE_LOCAL_MODEL", "false").lower() == "true"

print(f"{Fore.CYAN}Initializing LothbrokAI Game Master CLI...{Style.RESET_ALL}")

# Setup Client
if USE_LOCAL:
    client = OpenAI(base_url=LOCAL_URL, api_key="not-needed")
    print(f"[{Fore.GREEN}INFO{Style.RESET_ALL}] Routing via Local LMStudio")
else:
    client = OpenAI(base_url="https://openrouter.ai/api/v1", api_key=API_KEY)
    print(f"[{Fore.GREEN}INFO{Style.RESET_ALL}] Routing via OpenRouter ({MODEL})")

# Fetch Live Graph Data from Bannerlord Engine
API_ENDPOINT = "http://localhost:8080/api/graph"
print(f"[{Fore.GREEN}INFO{Style.RESET_ALL}] Fetching Live Graph from {API_ENDPOINT} ...")

try:
    import urllib.request
    with urllib.request.urlopen(API_ENDPOINT) as response:
        raw_graph = json.loads(response.read().decode())
except Exception as e:
    print(f"{Fore.RED}[FATAL] Could not connect to Bannerlord Game Master API. Is the game running and Campaign loaded? Error: {str(e)}{Style.RESET_ALL}")
    sys.exit(1)

# Build NetworkX Graph for heavy math
G = nx.DiGraph()
for node in raw_graph["nodes"]:
    G.add_node(node["id"], **node)
for edge in raw_graph["edges"]:
    G.add_edge(edge["source"], edge["target"], **edge)

print(f"[{Fore.GREEN}INFO{Style.RESET_ALL}] Loaded Graph: {G.number_of_nodes()} Nodes, {G.number_of_edges()} Edges")

# =====================================================================
# TOOL IMPLEMENTATIONS (Graph Traversal)
# =====================================================================

def query_entity(name_query: str) -> str:
    """Finds all nodes matching the given name and returns their IDs and basic properties."""
    results = []
    for node_id, data in G.nodes(data=True):
        props = data.get("properties", {})
        if "name" in props and name_query.lower() in props["name"].lower():
            results.append({"id": node_id, "type": data.get("type"), "properties": props})
    return json.dumps(results[:5]) # Limit to top 5 to save tokens

def get_relations(node_id: str) -> str:
    """Returns all outbound and inbound relationships for a specific node ID."""
    if node_id not in G:
        return json.dumps({"error": f"Node {node_id} not found."})
    
    relations = []
    for _, target, data in G.out_edges(node_id, data=True):
        target_name = G.nodes[target].get("properties", {}).get("name", "Unknown")
        relations.append(f"--[{data.get('type')}]--> {target_name} ({target})")
        
    for source, _, data in G.in_edges(node_id, data=True):
        source_name = G.nodes[source].get("properties", {}).get("name", "Unknown")
        relations.append(f"<--[{data.get('type')}]-- {source_name} ({source})")
        
    return "\n".join(relations)

def find_weakest_vassal(kingdom_id: str) -> str:
    """Finds the weakest clan belonging to a specific kingdom based on Tier."""
    if kingdom_id not in G:
        return json.dumps({"error": f"Kingdom {kingdom_id} not found."})
        
    vassals = []
    for source, _, data in G.in_edges(kingdom_id, data=True):
        if data.get("type") == "VASSAL_OF":
            clan_node = G.nodes[source]
            vassals.append({"id": source, "name": clan_node.get("properties", {}).get("name"), "tier": clan_node.get("properties", {}).get("tier", 99)})
            
    if not vassals:
        return "No vassals found."
        
    vassals.sort(key=lambda x: x["tier"])
    return json.dumps(vassals[:3]) # Return lowest 3 tier clans

# =====================================================================
# TOOL REGISTRY & LLM LOOP
# =====================================================================

tools = [
    {
        "type": "function",
        "function": {
            "name": "query_entity",
            "description": "Finds a Lord, Settlement, Clan, or Kingdom by name to get its exact ID.",
            "parameters": {
                "type": "object",
                "properties": {
                    "name_query": {"type": "string", "description": "The name to search for (e.g., 'Derthert' or 'Vlandia')"}
                },
                "required": ["name_query"]
            }
        }
    },
    {
        "type": "function",
        "function": {
            "name": "get_relations",
            "description": "Gets all relationships (friends, enemies, vassals, owners) for a specific Node ID.",
            "parameters": {
                "type": "object",
                "properties": {
                    "node_id": {"type": "string", "description": "The exact node ID (e.g., 'H_Derthert' or 'K_Vlandia')"}
                },
                "required": ["node_id"]
            }
        }
    },
    {
        "type": "function",
        "function": {
            "name": "find_weakest_vassal",
            "description": "Finds the lowest tier target clan in a kingdom.",
            "parameters": {
                "type": "object",
                "properties": {
                    "kingdom_id": {"type": "string", "description": "The Kingdom Node ID (e.g., 'K_vlandia')"}
                },
                "required": ["kingdom_id"]
            }
        }
    }
]

messages = [
    {"role": "system", "content": "You are the Game Master Navigator for Mount & Blade Bannerlord. You use the provided tools to traverse the Calradia JSON Graph and answer the player's strategic questions. Always find exact IDs first before querying relations."}
]

print(f"\n{Fore.YELLOW}Welcome to the LothbrokAI Navigator. Type 'exit' to quit.{Style.RESET_ALL}\n")

while True:
    user_input = input(f"{Fore.MAGENTA}Commander > {Style.RESET_ALL}")
    if user_input.lower() in ['exit', 'quit']:
        break

    messages.append({"role": "user", "content": user_input})

    response = client.chat.completions.create(
        model=MODEL,
        messages=messages,
        tools=tools,
        tool_choice="auto"
    )

    response_message = response.choices[0].message
    messages.append(response_message)

    # Execute tools if requested
    if response_message.tool_calls:
        for tool_call in response_message.tool_calls:
            function_name = tool_call.function.name
            args = json.loads(tool_call.function.arguments)
            
            print(f"  {Fore.CYAN}[System]{Style.RESET_ALL} Executing {function_name}({args})...")
            
            result = ""
            if function_name == "query_entity":
                result = query_entity(args.get("name_query"))
            elif function_name == "get_relations":
                result = get_relations(args.get("node_id"))
            elif function_name == "find_weakest_vassal":
                result = find_weakest_vassal(args.get("kingdom_id"))
                
            messages.append({
                "role": "tool",
                "tool_call_id": tool_call.id,
                "name": function_name,
                "content": result
            })
            
        # Send results back to LLM for final answer
        final_response = client.chat.completions.create(
            model=MODEL,
            messages=messages
        )
        answer = final_response.choices[0].message.content
        messages.append({"role": "assistant", "content": answer})
        print(f"\n{Fore.GREEN}Navigator >{Style.RESET_ALL} {answer}\n")
    else:
        answer = response_message.content
        print(f"\n{Fore.GREEN}Navigator >{Style.RESET_ALL} {answer}\n")
