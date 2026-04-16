using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace LothbrokAI.Quests
{
    /// <summary>
    /// QuestEngine binds the LLM's narrative quest requests to physical Bannerlord mechanics.
    /// It translates generic requests into concrete, trackable objectives (parties, settlements).
    /// </summary>
    public static class QuestEngine
    {
        public static void GenerateQuest(Hero questGiver, Hero player, string questText, int magnitude)
        {
            // DESIGN: Instead of a full IssueBase implementation (which requires XML and complex 
            // SaveableTypeDefiner hooks that often break saves), we create "Virtual Quests" tracked
            // in MemoryEngine or as RP Items for now.
            // A virtual quest forces the player to return to the NPC and use dialogue to resolve it.
            
            // Try to find a real target near the questGiver's settlement to make the quest authentic
            string targetDesc = FindAuthenticTarget(questGiver);

            string questName = $"Task for {questGiver.Name}";
            string fullDescription = $"{questText}\n\nSuggested Target: {targetDesc}";

            // Register as an RP Item so the AI Remembers it
            LetterSystem.CreateRPItem($"Quest: {questName}", fullDescription);
            
            LothbrokSubModule.Log($"QuestEngine: Generated quest '{questName}' targeting {targetDesc}", TaleWorlds.Library.Debug.DebugColor.Yellow);
            TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage($"New AI Task: {questName}", TaleWorlds.Library.Color.FromUint(0xFFD700FF)));
        }

        private static string FindAuthenticTarget(Hero questGiver)
        {
            if (questGiver.CurrentSettlement != null)
            {
                // Find a random bandit party
                var nearbyBandits = MobileParty.All.FirstOrDefault(p => p.IsBandit);

                if (nearbyBandits != null)
                {
                    return $"Bandit presence near {questGiver.CurrentSettlement.Name}";
                }
                
                // Find an enemy settlement
                if (questGiver.Clan != null && questGiver.Clan.Kingdom != null)
                {
                    var enemyFaction = TaleWorlds.CampaignSystem.Campaign.Current.Kingdoms
                        .FirstOrDefault(k => k.IsAtWarWith(questGiver.Clan.Kingdom));
                        
                    if (enemyFaction != null)
                    {
                        var enemySettlement = enemyFaction.Settlements.FirstOrDefault();
                        if (enemySettlement != null)
                            return $"The heavily guarded {enemySettlement.Name}";
                    }
                }
            }

            return "Unknown forces on the campaign map.";
        }
    }
}
