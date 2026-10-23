global using HarmonyLib;
global using System.Collections.Generic;

using System;
using System.IO;
using AmongUs.GameOptions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace TaskDoorTweaks;

// Standalone mini-mod. Three independent features, each patched and toggled separately so
// one breaking doesn't take the others down:
//   1. Lets you disable any task type (SubmitScan / "Medbay scan" is on by default) so it's
//      never handed out.
//   2. Lets you set how many seconds the Polus decontamination doors take to cycle.
//   3. Lets you "unlock" the step size and min/max range of every vanilla numeric host
//      setting (vision, kill cooldown, task counts, etc) instead of being stuck with
//      vanilla's fixed increments and bounds.
[BepInPlugin(Id, Name, Version)]
[BepInProcess("Among Us.exe")]
public class Plugin : BasePlugin
{
    public const string Id = "com.katzklaw.taskdoortweaks";
    public const string Name = "TaskDoorTweaks";
    public const string Version = "1.3.1";

    // BepInEx writes config sections in alphabetical order, not in the order they're bound.
    // "Unlock Override - " sorts after "Tasks", so the per-setting overrides land at the very
    // bottom of the file instead of above the sections people actually edit. (Pre-1.3.1 they
    // were named "Override - ", which sorted into the middle; old values are migrated below.)
    public const string OverridePrefix = "Unlock Override - ";
    private const string LegacyOverridePrefix = "Override - ";

    public static Plugin Instance { get; private set; }
    public static ManualLogSource Logger { get; private set; }
    public static ConfigFile Cfg { get; private set; }

    public Harmony Harmony { get; } = new(Id);

    // --- Polus decontamination doors -------------------------------------------------
    public static ConfigEntry<float> PolusDeconSeconds { get; private set; }

    // --- Per-task disable toggles ------------------------------------------------------
    public static Dictionary<TaskTypes, ConfigEntry<bool>> DisableTask { get; } = new();

    // --- Vanilla numeric setting unlock (step size + min/max) --------------------------
    public static ConfigEntry<bool> UnlockNumberOptions { get; private set; }
    public static ConfigEntry<float> DecimalStep { get; private set; }
    public static ConfigEntry<float> WholeStep { get; private set; }
    public static ConfigEntry<float> MinDecimal { get; private set; }
    public static ConfigEntry<float> MinWhole { get; private set; }
    public static ConfigEntry<float> MaxMultiplier { get; private set; }

    public override void Load()
    {
        Instance = this;
        Logger = base.Log;
        Cfg = base.Config;

        BindDeconOptions();
        BindTaskOptions();
        BindUnlockOptions();
        CleanUpConfig();

        // Each patch class is applied independently and wrapped in try/catch on purpose:
        // Harmony.PatchAll() bails out entirely on the first bind failure, so one broken
        // patch (e.g. after a game update renames a member) would otherwise silently take
        // out every feature in the mod, not just itself.
        PatchResilient(typeof(Patches.TaskDisablePatch));
        PatchResilient(typeof(Patches.DeconDoorPatch));
        PatchResilient(typeof(Patches.OptionUnlockPatch));

        Logger.LogInfo($"{Name} v{Version} loaded.");
    }

    private void PatchResilient(Type patchType)
    {
        try
        {
            Harmony.PatchAll(patchType);
            Logger.LogInfo($"Patched {patchType.Name}");
        }
        catch (Exception e)
        {
            Logger.LogError($"Failed to patch {patchType.Name} — that feature is disabled, everything else still runs: {e}");
        }
    }

    // BepInEx keeps any entry it finds in the .cfg that nothing has bound ("orphans") and writes
    // it straight back on every save, with no comments. That is how dead sections from older
    // versions (e.g. [GameKillCooldown] / [CapacityLabel] with Step/Min/Max) stuck around forever,
    // and why the per-setting override sections lose their comments until the menu is opened.
    //
    // BepInEx 6 doesn't expose the orphan list, so this tidies the file text itself and then asks
    // BepInEx to reload it. It runs once at load, after the always-present sections are bound:
    //   - [Polus Decontamination Doors], [Setting Unlock] and [Tasks] are kept exactly as they are
    //   - override sections (old "Override - " or new "Unlock Override - " names) are kept only if
    //     the user actually changed something in them, and are renamed to the new name
    //   - everything else is dead and is dropped
    // Override sections are simply recreated, with comments, the next time the host menu is opened.
    private static readonly string[] KeptSections = { "Polus Decontamination Doors", "Setting Unlock", "Tasks" };

    private void CleanUpConfig()
    {
        try
        {
            string path = Cfg.ConfigFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            string original = File.ReadAllText(path);
            string[] lines = original.Replace("\r\n", "\n").Split('\n');

            var output = new List<string>();
            var emitted = new HashSet<string>();
            string section = null;
            var block = new List<string>();
            int dropped = 0, kept = 0;

            void Flush()
            {
                if (section == null)
                {
                    output.AddRange(block); // file header comments
                }
                else if (Array.IndexOf(KeptSections, section) >= 0)
                {
                    output.Add("[" + section + "]");
                    output.AddRange(block);
                }
                else
                {
                    string newSection = null;
                    if (section.StartsWith(OverridePrefix, StringComparison.Ordinal))
                        newSection = section;
                    else if (section.StartsWith(LegacyOverridePrefix, StringComparison.Ordinal))
                        newSection = OverridePrefix + section.Substring(LegacyOverridePrefix.Length);

                    var changed = new List<string>();
                    if (newSection != null)
                    {
                        foreach (string raw in block)
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith("#")) continue;
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            string key = line.Substring(0, eq).Trim();
                            string value = line.Substring(eq + 1).Trim();
                            bool isDefault = key == "Skip"
                                ? !string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                                : value.Length == 0;
                            if (!isDefault) changed.Add($"{key} = {value}");
                        }
                    }

                    if (newSection != null && changed.Count > 0 && emitted.Add(newSection))
                    {
                        output.Add("[" + newSection + "]");
                        output.Add("");
                        foreach (string c in changed) { output.Add(c); output.Add(""); }
                        kept++;
                    }
                    else
                    {
                        dropped++;
                    }
                }
                block.Clear();
            }

            foreach (string raw in lines)
            {
                string t = raw.Trim();
                if (t.Length > 2 && t[0] == '[' && t[t.Length - 1] == ']')
                {
                    Flush();
                    section = t.Substring(1, t.Length - 2);
                }
                else
                {
                    block.Add(raw);
                }
            }
            Flush();

            string cleaned = string.Join("\n", output).TrimEnd('\n') + "\n";
            if (cleaned == original.Replace("\r\n", "\n")) return; // nothing to do

            File.WriteAllText(path, cleaned.Replace("\n", Environment.NewLine));
            Cfg.Reload();
            Logger.LogInfo($"Config tidy: removed {dropped} stale/blank sections, kept {kept} customised override sections.");
        }
        catch (Exception e)
        {
            Logger.LogWarning($"Config tidy skipped (harmless, the file just stays as it was): {e.Message}");
        }
    }

    private void BindDeconOptions()
    {
        PolusDeconSeconds = Cfg.Bind(
            "Polus Decontamination Doors",
            "DoorCycleSeconds",
            3f,
            new ConfigDescription(
                "Seconds the Polus decontamination doors take to open/close AND the seconds " +
                "spent decontaminating in the chamber (both are set to this same value, so a " +
                "full in-and-out round trip is double whatever you set here). Vanilla is 3 " +
                "(round trip 6). This is a plain typed-in value, not an in-game slider, so any " +
                "decimal works, not just halves — 0.5 gives the fastest possible round trip of " +
                "1 second total, and e.g. 2.25 works just as well as 2 or 2.5 if you want it.",
                new AcceptableValueRange<float>(0.5f, 6f)));
    }

    private void BindUnlockOptions()
    {
        UnlockNumberOptions = Cfg.Bind(
            "Setting Unlock",
            "Enabled",
            true,
            "Master switch. When on, every +/- stepper in the host settings menu (vision, speed, " +
            "kill cooldown, task counts, role settings, ...) gets finer steps and a wider range " +
            "using the global rules below. Turn off to put all of them back to stock vanilla. " +
            "The max-players range (4-15) is never changed. Settings only change when the menu is " +
            "built, so re-open the host settings menu (or restart the lobby) after editing this file. " +
            "Per-setting 'Unlock Override - <name>' sections appear at the very bottom of this file " +
            "after you open the menu once, for forcing or skipping a single setting.");

        DecimalStep = Cfg.Bind(
            "Setting Unlock",
            "DecimalStep",
            0.05f,
            new ConfigDescription(
                "Step size for settings that vanilla moves in steps smaller than 1 (player speed, " +
                "crew/impostor vision, ...). 0.05 lets you reach values like 1.3 or 0.55.",
                new AcceptableValueRange<float>(0.01f, 1f)));

        WholeStep = Cfg.Bind(
            "Setting Unlock",
            "WholeStep",
            1f,
            new ConfigDescription(
                "Largest step size for settings that vanilla moves in steps of 1 or more (task counts, " +
                "cooldowns, meeting times, ...). Vanilla steps that are already smaller are kept.",
                new AcceptableValueRange<float>(0.05f, 100f)));

        MinDecimal = Cfg.Bind(
            "Setting Unlock",
            "MinDecimal",
            0.05f,
            new ConfigDescription(
                "Lowest value allowed for the decimal settings above (never raises vanilla's own minimum).",
                new AcceptableValueRange<float>(0f, 1f)));

        MinWhole = Cfg.Bind(
            "Setting Unlock",
            "MinWhole",
            0f,
            new ConfigDescription(
                "Lowest value allowed for the whole-number settings above (never raises vanilla's own " +
                "minimum). 0 lets counts like detective cases go below vanilla's minimum.",
                new AcceptableValueRange<float>(0f, 100f)));

        MaxMultiplier = Cfg.Bind(
            "Setting Unlock",
            "MaxMultiplier",
            2f,
            new ConfigDescription(
                "New maximum = vanilla's maximum x this number. 1 keeps vanilla's maximum.",
                new AcceptableValueRange<float>(1f, 100f)));
    }

    private void BindTaskOptions()
    {
        // Full TaskTypes list, taken from a confirmed-working reference (ApeMV/AmongUsRevamped's
        // own task-disable patch) rather than guessed from NuGet metadata, per the known
        // runtime-vs-compile-time gap in AmongUs.GameLibs.Steam. Everything defaults to
        // "not disabled" except SubmitScan, since turning that one off is the actual point
        // of this mod — flip any of the others to true in the config file if you want them too.
        BindTask(TaskTypes.SubmitScan, true, "This is the Medbay 'scan' task.");
        BindTask(TaskTypes.SwipeCard);
        BindTask(TaskTypes.UnlockSafe);
        BindTask(TaskTypes.UploadData);
        BindTask(TaskTypes.StartReactor);
        BindTask(TaskTypes.ResetBreakers);
        BindTask(TaskTypes.VentCleaning);
        BindTask(TaskTypes.CalibrateDistributor);
        BindTask(TaskTypes.ChartCourse);
        BindTask(TaskTypes.StabilizeSteering);
        BindTask(TaskTypes.CleanO2Filter);
        BindTask(TaskTypes.UnlockManifolds);
        BindTask(TaskTypes.PrimeShields);
        BindTask(TaskTypes.MeasureWeather);
        BindTask(TaskTypes.BuyBeverage);
        BindTask(TaskTypes.AssembleArtifact);
        BindTask(TaskTypes.SortSamples);
        BindTask(TaskTypes.ProcessData);
        BindTask(TaskTypes.RunDiagnostics);
        BindTask(TaskTypes.RepairDrill);
        BindTask(TaskTypes.AlignTelescope);
        BindTask(TaskTypes.RecordTemperature);
        BindTask(TaskTypes.FillCanisters);
        BindTask(TaskTypes.MonitorOxygen);
        BindTask(TaskTypes.StoreArtifacts);
        BindTask(TaskTypes.PutAwayPistols);
        BindTask(TaskTypes.PutAwayRifles);
        BindTask(TaskTypes.MakeBurger);
        BindTask(TaskTypes.CleanToilet);
        BindTask(TaskTypes.Decontaminate);
        BindTask(TaskTypes.SortRecords);
        BindTask(TaskTypes.FixShower);
        BindTask(TaskTypes.PickUpTowels);
        BindTask(TaskTypes.PolishRuby);
        BindTask(TaskTypes.DressMannequin);
        BindTask(TaskTypes.AlignEngineOutput);
        BindTask(TaskTypes.InspectSample);
        BindTask(TaskTypes.EmptyChute);
        BindTask(TaskTypes.ClearAsteroids);
        BindTask(TaskTypes.WaterPlants);
        BindTask(TaskTypes.OpenWaterways);
        BindTask(TaskTypes.ReplaceWaterJug);
        BindTask(TaskTypes.RebootWifi);
        BindTask(TaskTypes.DevelopPhotos);
        BindTask(TaskTypes.RewindTapes);
        BindTask(TaskTypes.StartFans);
        BindTask(TaskTypes.FixWiring);
        BindTask(TaskTypes.EnterIdCode);
        BindTask(TaskTypes.InsertKeys);
        BindTask(TaskTypes.ScanBoardingPass);
        BindTask(TaskTypes.EmptyGarbage);
        BindTask(TaskTypes.FuelEngines);
        BindTask(TaskTypes.DivertPower);
        BindTask(TaskTypes.FixWeatherNode);
        BindTask(TaskTypes.RoastMarshmallow);
        BindTask(TaskTypes.CollectSamples);
        BindTask(TaskTypes.ReplaceParts);
        BindTask(TaskTypes.CollectVegetables);
        BindTask(TaskTypes.MineOres);
        BindTask(TaskTypes.ExtractFuel);
        BindTask(TaskTypes.CatchFish);
        BindTask(TaskTypes.PolishGem);
        BindTask(TaskTypes.HelpCritter);
        BindTask(TaskTypes.HoistSupplies);
        BindTask(TaskTypes.FixAntenna);
        BindTask(TaskTypes.BuildSandcastle);
        BindTask(TaskTypes.CrankGenerator);
        BindTask(TaskTypes.MonitorMushroom);
        BindTask(TaskTypes.PlayVideogame);
        BindTask(TaskTypes.TuneRadio);
        BindTask(TaskTypes.TestFrisbee);
        BindTask(TaskTypes.LiftWeights);
        BindTask(TaskTypes.CollectShells);
    }

    private void BindTask(TaskTypes type, bool defaultValue = false, string extraDescription = null)
    {
        string description = $"Disable the '{type}' task — it will never be assigned to anyone.";
        if (!string.IsNullOrEmpty(extraDescription))
            description += " " + extraDescription;

        DisableTask[type] = Cfg.Bind("Tasks", $"Disable_{type}", defaultValue, description);
    }
}
