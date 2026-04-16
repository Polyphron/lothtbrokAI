using System;
using LothbrokAI.Memory;
using TaleWorlds.CampaignSystem;

namespace LothbrokAI.Core
{
    /// <summary>
    /// Hooks into Bannerlord's save/load lifecycle to manage our custom memory data.
    /// DESIGN: We do NOT use SaveableTypeDefiner. All LothbrokAI data is stored in
    /// external JSON sidecars per campaign. This prevents the campaign's main save
    /// file from bloating to hundreds of megabytes (like AIInfluence did).
    /// </summary>
    public class SaveBehavior : CampaignBehaviorBase
    {
        public override void RegisterEvents()
        {
            // Flush all in-memory changes to JSON when the player saves the game
            CampaignEvents.OnBeforeSaveEvent.AddNonSerializedListener(this, OnBeforeSave);
            
            // Periodically save just in case of unexpected crashes (every game day)
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // DESIGN: Hybrid Save. Memory logs go to JSON, but the hardcore political math and 
            // state (Reputation, Favors, Leverage, Rumors, Factions) go directly into the save.
            
            dataStore.SyncData("lothbrok_player_honor", ref Medici.MediciState.PlayerHonor);
            dataStore.SyncData("lothbrok_player_fear", ref Medici.MediciState.PlayerFear);
            dataStore.SyncData("lothbrok_player_influence", ref Medici.MediciState.PlayerInfluence);

            // Objects must be serialized via JSON for SyncData if we don't declare SaveableTypeDefiner
            string favorsJson = "";
            string rumorsJson = "";
            string leverageJson = "";
            string factionsJson = "";
            string rpItemsJson = "";

            if (dataStore.IsSaving)
            {
                favorsJson = Newtonsoft.Json.JsonConvert.SerializeObject(Medici.MediciState.FavorsOwedToPlayer);
                rumorsJson = Newtonsoft.Json.JsonConvert.SerializeObject(Medici.MediciState.ActiveRumors);
                leverageJson = Newtonsoft.Json.JsonConvert.SerializeObject(Medici.MediciState.PlayerLeverage);
                factionsJson = Newtonsoft.Json.JsonConvert.SerializeObject(Medici.MediciState.HeroFactions);
                rpItemsJson = Newtonsoft.Json.JsonConvert.SerializeObject(Quests.LetterSystem.PlayerRPItems);
            }

            dataStore.SyncData("lothbrok_favors", ref favorsJson);
            dataStore.SyncData("lothbrok_rumors", ref rumorsJson);
            dataStore.SyncData("lothbrok_leverage", ref leverageJson);
            dataStore.SyncData("lothbrok_factions", ref factionsJson);
            dataStore.SyncData("lothbrok_rpitems", ref rpItemsJson);

            if (dataStore.IsLoading)
            {
                if (!string.IsNullOrEmpty(favorsJson))
                    Medici.MediciState.FavorsOwedToPlayer = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Medici.FavorData>>>(favorsJson);
                
                if (!string.IsNullOrEmpty(rumorsJson))
                    Medici.MediciState.ActiveRumors = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, Medici.RumorData>>(rumorsJson);
                
                if (!string.IsNullOrEmpty(leverageJson))
                    Medici.MediciState.PlayerLeverage = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>>(leverageJson);
                
                if (!string.IsNullOrEmpty(factionsJson))
                    Medici.MediciState.HeroFactions = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, Medici.PoliticalFaction>>(factionsJson);

                if (!string.IsNullOrEmpty(rpItemsJson))
                    Quests.LetterSystem.PlayerRPItems = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, string>>(rpItemsJson);
            }
        }

        private void OnBeforeSave()
        {
            MemoryEngine.FlushAll();
            LothbrokSubModule.Log("Memory graph flushed " + 
                "(synced alongside Bannerlord save: " + Campaign.Current.UniqueGameId + ")", 
                TaleWorlds.Library.Debug.DebugColor.Green);
        }

        private void OnDailyTick()
        {
            // Backup save to prevent losing recent conversation history
            MemoryEngine.FlushAll();
        }
    }
}
