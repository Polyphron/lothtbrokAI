using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Pages;
using LothbrokAI.Core;
using LothbrokAI.Memory;

namespace LothbrokAI.Patches
{
    [HarmonyPatch(typeof(EncyclopediaHeroPageVM), "RefreshValues")]
    public static class EncyclopediaHeroPageVMPatch
    {
        public static void Postfix(EncyclopediaHeroPageVM __instance)
        {
            if (__instance.Obj is Hero hero)
            {
                var payload = MemoryEngine.Retrieve(ContextAssembler.GetNpcId(hero), hero.Name.ToString(), "");
                
                if (!string.IsNullOrEmpty(payload.Personality))
                {
                    string notes = __instance.InformationText;
                    
                    if (notes != null && !notes.Contains("[LothbrokAI Personality]"))
                    {
                        __instance.InformationText = notes + "\n\n[LothbrokAI Personality]: " + payload.Personality;
                        
                        if (!string.IsNullOrEmpty(payload.Backstory))
                        {
                            __instance.InformationText += "\n[Backstory]: " + payload.Backstory;
                        }
                    }
                    else if (notes == null)
                    {
                        __instance.InformationText = "[LothbrokAI Personality]: " + payload.Personality;
                        
                        if (!string.IsNullOrEmpty(payload.Backstory))
                        {
                            __instance.InformationText += "\n[Backstory]: " + payload.Backstory;
                        }
                    }
                }
            }
        }
    }
}
