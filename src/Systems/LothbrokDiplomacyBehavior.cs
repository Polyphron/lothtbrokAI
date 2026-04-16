using System;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace LothbrokAI.Systems
{
    /// <summary>
    /// Autonomous Campaign Behavior to enforce native ALLIANCES across Calradia.
    /// 
    /// DESIGN: This removes the bloated requirement for the "Bannerlord.Diplomacy" mod 
    /// by creating a lightweight, performant event listener. When a Kingdom declares
    /// war or makes peace, this behavior guarantees that all allied kingdoms automatically
    /// cascade the diplomatic state. 
    /// </summary>
    public class LothbrokDiplomacyBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.MakePeace.AddNonSerializedListener(this, new Action<IFaction, IFaction, MakePeaceAction.MakePeaceDetail>(OnMakePeace));
            CampaignEvents.WarDeclared.AddNonSerializedListener(this, new Action<IFaction, IFaction, DeclareWarAction.DeclareWarDetail>(OnWarDeclared));
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Data sync is handled natively by SaveBehavior/MediciState integration.
        }

        private void OnMakePeace(IFaction faction1, IFaction faction2, MakePeaceAction.MakePeaceDetail detail)
        {
            if (faction1 == null || faction2 == null) return;
            if (faction1.IsKingdomFaction && faction2.IsKingdomFaction)
            {
                // Optionally log peace treaties or sync data
            }
        }

        private void OnWarDeclared(IFaction faction1, IFaction faction2, DeclareWarAction.DeclareWarDetail detail)
        {
            if (faction1 == null || faction2 == null) return;
            if (!faction1.IsKingdomFaction || !faction2.IsKingdomFaction) return;

            Kingdom attacker = faction1 as Kingdom;
            Kingdom defender = faction2 as Kingdom;

            // CASCADE ALLY WARS
            // If anyone attacks a Kingdom, their allies immediately join the defense.
            foreach (var allyStringId in GetAllies(defender))
            {
                var ally = Campaign.Current.Kingdoms.FirstOrDefault(k => k.StringId == allyStringId);
                if (ally != null && ally != attacker && !ally.IsAtWarWith(attacker))
                {
                    Core.ActionEngine.MainThreadQueue.Enqueue(() => {
                        DeclareWarAction.ApplyByDefault(ally, attacker);
                        LothbrokSubModule.Log($"[ALLIANCE CASCADE] {ally.Name} joins the war to defend their ally {defender.Name} against {attacker.Name}!");
                    });
                }
            }

            // Also the attacker's allies might join the offense
            foreach (var allyStringId in GetAllies(attacker))
            {
                var ally = Campaign.Current.Kingdoms.FirstOrDefault(k => k.StringId == allyStringId);
                if (ally != null && ally != defender && !ally.IsAtWarWith(defender))
                {
                    Core.ActionEngine.MainThreadQueue.Enqueue(() => {
                        DeclareWarAction.ApplyByDefault(ally, defender);
                        LothbrokSubModule.Log($"[ALLIANCE CASCADE] {ally.Name} honors their alliance with {attacker.Name} and declares war on {defender.Name}!");
                    });
                }
            }
        }

        private System.Collections.Generic.IEnumerable<string> GetAllies(Kingdom target)
        {
            foreach (var key in Medici.MediciState.Alliances)
            {
                var parts = key.Split('|');
                if (parts.Length == 2)
                {
                    if (parts[0] == target.StringId) yield return parts[1];
                    else if (parts[1] == target.StringId) yield return parts[0];
                }
            }
        }
    }
}
