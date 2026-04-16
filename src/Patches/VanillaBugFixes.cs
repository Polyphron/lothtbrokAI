using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.Library;

namespace LothbrokAI.Patches
{
    // ====================================================================
    // PORTED BUG FIXES FROM AIINFLUENCE
    // ====================================================================

    /// <summary>
    /// Fallback guard on ActivateGameMenu — ONLY blocks "town_outside".
    /// DESIGN: This captures a bug where a governor or hero following the player
    /// attempts to interact with a town settlement that doesn't exist, leading to a
    /// hard crash when the menu fails to activate.
    /// </summary>
    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.GameMenus.GameMenu), "ActivateGameMenu")]
    public static class ActivateGameMenuFallbackPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(900)]
        public static bool Prefix(string menuId)
        {
            if (menuId == "town_outside")
            {
                if (PlayerEncounter.Current == null || PlayerEncounter.EncounterSettlement == null)
                {
                    LothbrokSubModule.Log("Blocked ActivateGameMenu('town_outside'): No valid encounter.", TaleWorlds.Library.Debug.DebugColor.Red);
                    return false; // Skip the original method and avoid crashing
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Utility class to manually apply patches that require Reflection.
    /// E.g. DLC modules which aren't strongly typed in our assembly.
    /// </summary>
    public static class VanillaBugFixes
    {
        public static void ApplyManualPatches(Harmony harmony)
        {
            PatchShipOwnerChanged(harmony);
        }

        // ================================================================
        // PATCH: NavalDLC OnShipOwnerChanged NullRef
        // ================================================================

        /// <summary>
        /// Ships crash when transferring ownership if a town governor is away
        /// (e.g. following the player via LothbrokAI / AIInfluence).
        /// </summary>
        private static void PatchShipOwnerChanged(Harmony harmony)
        {
            Type shipTradeType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                shipTradeType = asm.GetType("NavalDLC.CampaignBehaviors.ShipTradeCampaignBehavior");
                if (shipTradeType != null) break;
            }

            if (shipTradeType == null)
            {
                LothbrokSubModule.Log("NavalDLC not found. Skipping Ship Owner patch.");
                return;
            }

            var targetMethod = AccessTools.Method(shipTradeType, "OnShipOwnerChanged");
            if (targetMethod == null) return;

            var prefix = new HarmonyMethod(typeof(ShipOwnerChangedGuard).GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public));
            prefix.priority = 900;
            harmony.Patch(targetMethod, prefix: prefix);

            LothbrokSubModule.Log("Patched NavalDLC OnShipOwnerChanged.", TaleWorlds.Library.Debug.DebugColor.Green);
        }
    }

    public static class ShipOwnerChangedGuard
    {
        public static bool Prefix(object ship, TaleWorlds.CampaignSystem.Party.PartyBase oldOwner, object details)
        {
            try
            {
                Hero governor = null;

                if (oldOwner != null && oldOwner.IsSettlement)
                {
                    var settlement = oldOwner.Settlement;
                    if (settlement?.Town != null) governor = settlement.Town.Governor;
                }

                if (governor == null && ship != null)
                {
                    var ownerProp = ship.GetType().GetProperty("Owner");
                    if (ownerProp != null)
                    {
                        var shipOwner = ownerProp.GetValue(ship) as TaleWorlds.CampaignSystem.Party.PartyBase;
                        if (shipOwner != null && shipOwner.IsSettlement)
                        {
                            var settlement = shipOwner.Settlement;
                            if (settlement?.Town != null) governor = settlement.Town.Governor;
                        }
                    }
                }

                if (governor == null) return true;

                if (governor.CurrentSettlement == null || governor.CurrentSettlement.Town == null)
                {
                    LothbrokSubModule.Log($"Blocked NullRef in OnShipOwnerChanged: Governor '{governor.Name}' has no Town context.", TaleWorlds.Library.Debug.DebugColor.Red);
                    return false; // Safely skip the failing code
                }
            }
            catch (Exception ex)
            {
                LothbrokSubModule.Log($"Guard exception in OnShipOwnerChanged: {ex.Message}", TaleWorlds.Library.Debug.DebugColor.Red);
                return false;
            }

            return true; 
        }
    }
}
