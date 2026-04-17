using System.Collections.Generic;
using TaleWorlds.Library;

namespace LothbrokAI.Testing
{
    /// <summary>
    /// Developer console commands for LothbrokAI.
    /// Type "lothbrok.test" in the Bannerlord console (Alt+~) to run the test harness.
    /// Type "lothbrok.graph" to export the Calradia graph.
    /// </summary>
    public static class ConsoleCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("test", "lothbrok")]
        public static string RunTests(List<string> args)
        {
            return LothbrokTestHarness.RunAllTests();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("graph", "lothbrok")]
        public static string ExportGraph(List<string> args)
        {
            if (TaleWorlds.CampaignSystem.Campaign.Current == null)
                return "FAIL: No active campaign.";

            try
            {
                string logDir = System.IO.Path.Combine(LothbrokSubModule.ModDir, "logs");
                if (!System.IO.Directory.Exists(logDir))
                    System.IO.Directory.CreateDirectory(logDir);

                string path = System.IO.Path.Combine(logDir, "calradia_graph.json");
                var result = Core.CalradiaGraphExporter.ExportGraph(path);
                return result;
            }
            catch (System.Exception ex)
            {
                return $"Graph export FAILED: {ex.Message}";
            }
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("status", "lothbrok")]
        public static string Status(List<string> args)
        {
            if (TaleWorlds.CampaignSystem.Campaign.Current == null)
                return "No active campaign.";

            int heroCount = 0;
            foreach (var h in TaleWorlds.CampaignSystem.Campaign.Current.AliveHeroes)
                heroCount++;

            return $"LothbrokAI v{LothbrokSubModule.MOD_VERSION} | Campaign: {TaleWorlds.CampaignSystem.Campaign.Current.UniqueGameId} | Heroes alive: {heroCount} | REST API: http://localhost:8080/";
        }
    }
}
