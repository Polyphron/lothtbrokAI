using System;
using System.Collections.Generic;
using TaleWorlds.Core;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace LothbrokAI.Medici
{
    /// <summary>
    /// MediciManager acts as the core political engine for LothbrokAI.
    /// It manages the daily simulation of social propagation, reputation decay, and factions.
    /// Uses CampaignBehaviorBase to hook into Bannerlord's time ticks.
    /// </summary>
    public class MediciManager : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourlyTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Data sync is handled exclusively by SaveBehavior.cs
        }

        // ================================================================
        // HOURLY PROCESSING: Propagation and Rumors
        // ================================================================

        private void OnHourlyTick()
        {
            // Process rumors spreading through the graph
            ProcessRumors();
        }

        private void ProcessRumors()
        {
            var expiredRumors = new List<string>();

            // Simulate word-of-mouth spread
            foreach (var kvp in MediciState.ActiveRumors)
            {
                var rumor = kvp.Value;
                // Rumors naturally decay over time (e.g. 10 game days)
                float age = (float)CampaignTime.Now.ToDays - rumor.CreatedDay;
                if (age > 10f)
                {
                    expiredRumors.Add(kvp.Key);
                    continue;
                }

                // Simplified gossip logic: 
                // Every hour, there's a small chance a rumor spreads to another hero.
                // In a heavy implementation, this would scan the "NodesReached" heroes' current
                // settlement and spread it to other heroes in the same room.
                if (MBRandom.RandomFloat < 0.05f) 
                {
                    // Logic for social propagation goes here internally
                }
            }

            foreach (var rId in expiredRumors)
            {
                MediciState.ActiveRumors.Remove(rId);
            }
        }

        // ================================================================
        // DAILY PROCESSING: Reputation and Factions
        // ================================================================

        private void OnDailyTick()
        {
            DecayReputation();
            // Optional: Re-evaluate faction clusters here if relationships have changed drastically
        }

        private void DecayReputation()
        {
            // Player reputation normalizes toward 0 over time
            float decayFactor = 0.98f; // Loses 2% of extreme value per day
            MediciState.PlayerHonor *= decayFactor;
            MediciState.PlayerFear *= decayFactor;
            MediciState.PlayerInfluence *= decayFactor;

            // Cap them strictly between -100 and 100
            MediciState.PlayerHonor = MathF.Clamp(MediciState.PlayerHonor, -100f, 100f);
            MediciState.PlayerFear = MathF.Clamp(MediciState.PlayerFear, -100f, 100f);
            MediciState.PlayerInfluence = MathF.Clamp(MediciState.PlayerInfluence, -100f, 100f);
        }

        // ================================================================
        // PUBLIC API FOR ACTION ENGINE
        // ================================================================

        public static void GrantFavor(Hero granter, Hero receiver, FavorMagnitude magnitude, string reason)
        {
            string gId = Core.ContextAssembler.GetNpcId(granter);
            string rId = Core.ContextAssembler.GetNpcId(receiver);
            
            // Only tracking favors involving the player for now
            if (!receiver.IsHumanPlayerCharacter && !granter.IsHumanPlayerCharacter)
                return;

            string targetTracking = receiver.IsHumanPlayerCharacter ? gId : rId;

            if (!MediciState.FavorsOwedToPlayer.ContainsKey(targetTracking))
            {
                MediciState.FavorsOwedToPlayer[targetTracking] = new List<FavorData>();
            }

            // If granter = player, the receiver owes the player.
            // Wait, standard Medici logic: we track what the WORLD owes the PLAYER.
            if (granter.IsHumanPlayerCharacter)
            {
                MediciState.FavorsOwedToPlayer[rId].Add(new FavorData()
                {
                    SubjectId = rId,
                    SourceId = gId,
                    Reason = reason,
                    Magnitude = (int)magnitude,
                    CreatedDay = (float)CampaignTime.Now.ToDays
                });
                
                LothbrokSubModule.Log($"Medici Engine: {receiver.Name} now owes the Player a {magnitude} favor ({reason})", TaleWorlds.Library.Debug.DebugColor.Green);
            }
        }

        public static void ModifyReputation(float honorDelta, float fearDelta, float influenceDelta)
        {
            MediciState.PlayerHonor = MathF.Clamp(MediciState.PlayerHonor + honorDelta, -100f, 100f);
            MediciState.PlayerFear = MathF.Clamp(MediciState.PlayerFear + fearDelta, -100f, 100f);
            MediciState.PlayerInfluence = MathF.Clamp(MediciState.PlayerInfluence + influenceDelta, -100f, 100f);
        }

        public static string GetPlayerReputationString()
        {
            string honor = MediciState.PlayerHonor > 30f ? "Honorable" : (MediciState.PlayerHonor < -30f ? "Dishonorable" : "Neutral");
            string fear = MediciState.PlayerFear > 30f ? "Feared" : (MediciState.PlayerFear < -30f ? "Scorned" : "Unimposing");
            string influence = MediciState.PlayerInfluence > 30f ? "Highly Influential" : (MediciState.PlayerInfluence < -30f ? "Irrelevant" : "Known");

            return $"Reputation: {honor}, {fear}, {influence}";
        }
    }
}
