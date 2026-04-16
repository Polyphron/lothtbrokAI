using System;
using System.Text;
using LothbrokAI.API;
using LothbrokAI.Memory;
using TaleWorlds.CampaignSystem;

namespace LothbrokAI.Core
{
    /// <summary>
    /// Generates personality, backstory, and speech quirks for NPCs on first contact.
    /// 
    /// DESIGN: Runs ONCE per NPC, on first conversation. Result is cached forever
    /// in the NPC's .lothbrok.json file. Uses the same API backend as regular
    /// dialogue. If API fails, falls back to a template-based personality.
    /// 
    /// Input: NPC's game attributes (culture, clan, age, skills, traits, role)
    /// Output: ~100 words personality + ~100 words backstory + 2-3 speech quirks
    /// </summary>
    public static class PersonalityGenerator
    {
        /// <summary>
        /// Generate personality for an NPC if one doesn't exist yet.
        /// Returns true if personality was generated (first contact).
        /// </summary>
        public static bool EnsurePersonality(string npcId, Hero hero)
        {
            var payload = MemoryEngine.Retrieve(npcId, hero.Name.ToString(), "");

            // Already has personality — skip
            if (!string.IsNullOrEmpty(payload.Personality))
                return false;

            LothbrokSubModule.Log("Generating personality for: " + hero.Name);

            string personality, backstory, quirks;

            try
            {
                // Build the generation prompt from game data
                string prompt = BuildGenerationPrompt(hero);

                var response = APIRouter.SendChatCompletion(
                    GetSystemPrompt(),
                    prompt);

                if (response.Success)
                {
                    ParseResponse(response.Text, out personality, out backstory, out quirks);
                }
                else
                {
                    LothbrokSubModule.Log("Personality gen failed, using template: " + response.Error,
                        TaleWorlds.Library.Debug.DebugColor.Yellow);
                    GenerateTemplateFallback(hero, out personality, out backstory, out quirks);
                }
            }
            catch (Exception ex)
            {
                LothbrokSubModule.Log("Personality gen exception: " + ex.Message,
                    TaleWorlds.Library.Debug.DebugColor.Yellow);
                GenerateTemplateFallback(hero, out personality, out backstory, out quirks);
            }

            // Save to memory (cached forever)
            MemoryEngine.SetPersonality(npcId, hero.Name.ToString(),
                personality, backstory, quirks);

            LothbrokSubModule.Log("Personality generated for " + hero.Name +
                " (" + TokenEstimator.Estimate(personality + backstory + quirks) + " tokens)");

            return true;
        }

        // ================================================================
        // PROMPT BUILDING
        // ================================================================

        private static string BuildGenerationPrompt(Hero hero)
        {
            var sb = new StringBuilder();

            sb.AppendLine("Generate a personality for this character:");
            sb.AppendLine();

            // Basic identity
            sb.AppendLine("Name: " + hero.Name);
            sb.AppendLine("Gender: " + (hero.IsFemale ? "Female" : "Male"));
            sb.AppendLine("Age: " + (int)hero.Age);

            // Culture
            if (hero.Culture != null)
                sb.AppendLine("Culture: " + hero.Culture.Name);

            // Clan and faction
            if (hero.Clan != null)
            {
                sb.AppendLine("Clan: " + hero.Clan.Name);
                if (hero.Clan.Kingdom != null)
                    sb.AppendLine("Kingdom: " + hero.Clan.Kingdom.Name);
                if (hero.Clan.Leader == hero)
                    sb.AppendLine("Role: Clan Leader");
            }

            // Social role
            sb.AppendLine("Social role: " + DetermineRole(hero));

            // Location
            if (hero.CurrentSettlement != null)
                sb.AppendLine("Current location: " + hero.CurrentSettlement.Name);

            // Key traits
            var traits = GetTraitDescription(hero);
            if (!string.IsNullOrEmpty(traits))
                sb.AppendLine("Personality traits: " + traits);

            // Combat skills (highest ones)
            sb.AppendLine("Notable skills: " + GetTopSkills(hero, 3));

            // Current state
            if (hero.IsPrisoner)
                sb.AppendLine("Status: PRISONER");
            if (hero.IsWounded)
                sb.AppendLine("Status: WOUNDED");

            // Spouse info
            if (hero.Spouse != null)
                sb.AppendLine("Spouse: " + hero.Spouse.Name);

            return sb.ToString();
        }

        private static string GetSystemPrompt()
        {
            return @"You are a character personality generator for a medieval world.
Given a character's attributes, generate THREE sections:

[PERSONALITY]
Write 2-3 sentences describing their temperament, motivations, and how they interact with others. Be specific to their culture and role.

[BACKSTORY]
Write 2-3 sentences of backstory that explains how they became who they are. Reference their culture, clan, and current situation.

[SPEECH]
List 2-3 speech quirks: accent, favorite expressions, verbal tics, formality level. Be specific (e.g. 'drops the letter h', 'always calls people lad/lass', 'speaks in third person when angry').

Keep it concise. Total response under 200 words.";
        }

        // ================================================================
        // RESPONSE PARSING
        // ================================================================

        private static void ParseResponse(string text,
            out string personality, out string backstory, out string quirks)
        {
            personality = ExtractSection(text, "[PERSONALITY]", "[BACKSTORY]");
            backstory = ExtractSection(text, "[BACKSTORY]", "[SPEECH]");
            quirks = ExtractSection(text, "[SPEECH]", null);

            // Fallback: if parsing fails, use entire response as personality
            if (string.IsNullOrEmpty(personality))
            {
                personality = text.Length > 300 ? text.Substring(0, 300) : text;
                backstory = "";
                quirks = "";
            }
        }

        private static string ExtractSection(string text, string startMarker, string endMarker)
        {
            int start = text.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";

            start += startMarker.Length;

            int end;
            if (endMarker != null)
            {
                end = text.IndexOf(endMarker, start, StringComparison.OrdinalIgnoreCase);
                if (end < 0) end = text.Length;
            }
            else
            {
                end = text.Length;
            }

            return text.Substring(start, end - start).Trim();
        }

        // ================================================================
        // TEMPLATE FALLBACK (no API needed)
        // ================================================================

        /// <summary>
        /// Generate a basic personality from game data alone.
        /// Used when API is unavailable.
        /// </summary>
        private static void GenerateTemplateFallback(Hero hero,
            out string personality, out string backstory, out string quirks)
        {
            string culture = hero.Culture != null ? hero.Culture.Name.ToString() : "unknown";
            string role = DetermineRole(hero);
            string name = hero.Name.ToString();

            personality = string.Format(
                "{0} is a {1} {2} of {3} heritage. {4}",
                name, GetTemperament(hero), role, culture,
                hero.IsFemale
                    ? "She carries herself with quiet determination."
                    : "He speaks with the directness of one used to command.");

            backstory = string.Format(
                "Born into clan {0}, {1} has known both hardship and opportunity. {2}",
                hero.Clan != null ? hero.Clan.Name.ToString() : "unknown",
                name,
                hero.Age > 40
                    ? "Years of service have etched lines on their face."
                    : "Youth still burns in their eyes.");

            quirks = GetCultureQuirks(culture);
        }

        // ================================================================
        // HELPER METHODS
        // ================================================================

        private static string DetermineRole(Hero hero)
        {
            if (hero.IsKingdomLeader) return "ruler";
            if (hero.Clan != null && hero.Clan.Leader == hero) return "clan leader";
            if (hero.GovernorOf != null) return "governor of " + hero.GovernorOf.Name;
            if (hero.IsPlayerCompanion) return "companion";
            if (hero.IsWanderer) return "wanderer";
            if (hero.IsNotable) return DetermineNotableRole(hero);
            if (hero.IsLord) return "lord";
            return "commoner";
        }

        private static string DetermineNotableRole(Hero hero)
        {
            // DESIGN: From AIInfluence changelog v3.3.6 — properly identify notable roles
            if (hero.IsGangLeader) return "gang leader";
            if (hero.IsMerchant) return "merchant";
            if (hero.IsPreacher) return "preacher";
            if (hero.IsRuralNotable) return "village headman";
            if (hero.IsArtisan) return "artisan";
            return "notable";
        }

        private static string GetTraitDescription(Hero hero)
        {
            var sb = new StringBuilder();

            // DESIGN: Bannerlord traits are -2 to +2 range
            // We only mention the strong ones (|value| >= 1)
            int valor = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Valor);
            int mercy = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Mercy);
            int honor = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Honor);
            int generosity = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Generosity);
            int calculating = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Calculating);

            if (valor >= 1) sb.Append("brave, ");
            if (valor <= -1) sb.Append("cautious, ");
            if (mercy >= 1) sb.Append("merciful, ");
            if (mercy <= -1) sb.Append("ruthless, ");
            if (honor >= 1) sb.Append("honorable, ");
            if (honor <= -1) sb.Append("deceitful, ");
            if (generosity >= 1) sb.Append("generous, ");
            if (generosity <= -1) sb.Append("greedy, ");
            if (calculating >= 1) sb.Append("calculating, ");
            if (calculating <= -1) sb.Append("impulsive, ");

            string result = sb.ToString().TrimEnd(' ', ',');
            return result;
        }

        private static string GetTopSkills(Hero hero, int count)
        {
            // Return the highest N skills
            var skills = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>();

            skills.Add(new System.Collections.Generic.KeyValuePair<string, int>(
                "One-Handed", hero.GetSkillValue(TaleWorlds.Core.DefaultSkills.OneHanded)));
            skills.Add(new System.Collections.Generic.KeyValuePair<string, int>(
                "Two-Handed", hero.GetSkillValue(TaleWorlds.Core.DefaultSkills.TwoHanded)));
            skills.Add(new System.Collections.Generic.KeyValuePair<string, int>(
                "Charm", hero.GetSkillValue(TaleWorlds.Core.DefaultSkills.Charm)));
            skills.Add(new System.Collections.Generic.KeyValuePair<string, int>(
                "Leadership", hero.GetSkillValue(TaleWorlds.Core.DefaultSkills.Leadership)));
            skills.Add(new System.Collections.Generic.KeyValuePair<string, int>(
                "Roguery", hero.GetSkillValue(TaleWorlds.Core.DefaultSkills.Roguery)));
            skills.Add(new System.Collections.Generic.KeyValuePair<string, int>(
                "Medicine", hero.GetSkillValue(TaleWorlds.Core.DefaultSkills.Medicine)));
            skills.Add(new System.Collections.Generic.KeyValuePair<string, int>(
                "Trade", hero.GetSkillValue(TaleWorlds.Core.DefaultSkills.Trade)));
            skills.Add(new System.Collections.Generic.KeyValuePair<string, int>(
                "Tactics", hero.GetSkillValue(TaleWorlds.Core.DefaultSkills.Tactics)));

            skills.Sort((a, b) => b.Value.CompareTo(a.Value));

            var sb = new StringBuilder();
            for (int i = 0; i < System.Math.Min(count, skills.Count); i++)
            {
                if (skills[i].Value > 50) // Only mention meaningful skills
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(skills[i].Key + " " + skills[i].Value);
                }
            }

            return sb.Length > 0 ? sb.ToString() : "unremarkable";
        }

        private static string GetTemperament(Hero hero)
        {
            int valor = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Valor);
            int mercy = hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Mercy);

            if (valor >= 1 && mercy >= 1) return "brave yet compassionate";
            if (valor >= 1 && mercy <= -1) return "fierce and ruthless";
            if (valor <= -1 && mercy >= 1) return "cautious but kind-hearted";
            if (valor <= -1 && mercy <= -1) return "cunning and cold";
            if (valor >= 1) return "bold";
            if (mercy >= 1) return "gentle";
            return "quiet";
        }

        private static string GetCultureQuirks(string culture)
        {
            // DESIGN: Culture-specific speech patterns for template fallback
            switch (culture.ToLowerInvariant())
            {
                case "sturgia":
                case "sturgian":
                    return "Speaks with a heavy northern accent. Uses 'by the frost' as an oath. Tends to be blunt.";
                case "battania":
                case "battanian":
                    return "Speaks with a lilting cadence. References the old forest spirits. Uses poetic metaphors.";
                case "vlandia":
                case "vlandian":
                    return "Formal and clipped speech. Fond of titles and proper address. Occasionally drops into old tongue phrases.";
                case "empire":
                case "western empire":
                case "northern empire":
                case "southern empire":
                    return "Educated speech with classical references. Uses Latin-sounding oaths. Somewhat pompous.";
                case "khuzait":
                    return "Direct and practical speech. Horse metaphors common. Says little but means much.";
                case "aserai":
                    return "Flowery and eloquent. Uses trade metaphors. Calls others 'friend' or 'traveler'.";
                default:
                    return "Speaks plainly. No notable accent.";
            }
        }
    }
}
