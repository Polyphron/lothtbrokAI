using System.Collections.Generic;

using TaleWorlds.CampaignSystem;

namespace LothbrokAI.Medici
{
    public enum FavorMagnitude
    {
        Minor = 1,
        Moderate = 2,
        Major = 3,
        LifeDebt = 5
    }

    public enum RumorSeverity
    {
        Harmless = 1,
        Damaging = 3,
        Treasonous = 5
    }

    public enum PoliticalFaction
    {
        Unaligned,
        Loyalists,
        Hawks,
        Doves,
        Reformists,
        Conspirators
    }

    /// <summary>
    /// Global tracking for Medici state. We do NOT use SaveableTypeDefiner here directly.
    /// Instead, we store generic primitives and serialize them inside SaveBehavior 
    /// using standard Bannerlord strings to avoid DLL save-compatibility issues.
    /// </summary>
    public static class MediciState
    {
        // Global Player Reputation (-100 to 100)
        public static float PlayerHonor = 0f;
        public static float PlayerFear = 0f;
        public static float PlayerInfluence = 0f;

        // Favors Owed TO the Player: Key = Hero StringId, Value = list of distinct favors
        public static Dictionary<string, List<FavorData>> FavorsOwedToPlayer = new Dictionary<string, List<FavorData>>();

        // Active Rumors propagating in the world
        public static Dictionary<string, RumorData> ActiveRumors = new Dictionary<string, RumorData>();

        // Player's Leverage against NPCs (Secret/Blackmail)
        public static Dictionary<string, List<string>> PlayerLeverage = new Dictionary<string, List<string>>();

        // Faction mappings per Hero
        public static Dictionary<string, PoliticalFaction> HeroFactions = new Dictionary<string, PoliticalFaction>();

        // Alliance and Pact Tracking (Phase 8.2 Standalone Diplomacy)
        // Key: "KingdomId1|KingdomId2" (alphabetically sorted to ensure unicity)
        public static HashSet<string> Alliances = new HashSet<string>();
        public static HashSet<string> NonAggressionPacts = new HashSet<string>();

        /// <summary>
        /// Generates a bidirectional deterministic key for two kingdom string IDs.
        /// </summary>
        public static string GetDiplomaticKey(string idA, string idB)
        {
            if (string.Compare(idA, idB, System.StringComparison.Ordinal) < 0)
                return idA + "|" + idB;
            return idB + "|" + idA;
        }
    }

    public class FavorData
    {
        public string SubjectId;   // Who owes the favor
        public string SourceId;    // Who the favor is owed to (usually Player "main_hero")
        public string Reason;
        public int Magnitude;      // (FavorMagnitude cast to int)
        public float CreatedDay;   
    }

    public class RumorData
    {
        public string RumorId;
        public string TargetId;    // The Hero the rumor is about
        public string Content;
        public int Severity;       // (RumorSeverity cast to int)
        public float CreatedDay;
        public bool IsProvenFalse;
        public List<string> NodesReached = new List<string>(); // Hero StringIds who "know" this
    }
}
