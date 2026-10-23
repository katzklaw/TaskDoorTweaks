using System;
using System.Globalization;
using AmongUs.GameOptions;
using BepInEx.Configuration;

namespace TaskDoorTweaks.Patches;

// Unlocks the +/- steppers in the host settings menu (kill cooldown, crew/impostor vision, player
// speed, task counts, role settings, voting/discussion time, ...) — anything the game draws with its
// own NumberOption widget.
//
// Why SetUpFromData and not Initialize: the in-game settings menu builds each widget from the
// game's setting data with NumberOption.SetUpFromData, which is where vanilla's step and range are
// assigned. Hooking after that call means our values are the last word. (An earlier version hooked
// NumberOption.Initialize, which only the separate max-players widget goes through — that is why
// only the player count ever changed.)
//
// Default behaviour with "Setting Unlock > Enabled" on, no per-setting editing needed:
//   - settings that vanilla moves in steps below 1 (speed, vision, ...) step by DecimalStep
//   - settings that vanilla moves in steps of 1 or more step by WholeStep at most
//   - the minimum drops to MinWhole / MinDecimal (never raised above vanilla's own minimum)
//   - the maximum becomes vanilla's maximum x MaxMultiplier
// Any single setting can still be overridden or skipped in its own "Unlock Override - <name>" section
// (named so it sorts to the bottom of the config file; see Plugin.OverridePrefix).
//
// The max-players range (4-15) is deliberately left at vanilla.
[HarmonyPatch(typeof(NumberOption), nameof(NumberOption.SetUpFromData))]
internal static class OptionUnlockPatch
{
    private static readonly HashSet<string> Seen = new();
    private static bool _errorLogged;

    public static void Postfix(NumberOption __instance)
    {
        try
        {
            if (!Plugin.UnlockNumberOptions.Value) return;

            string title = __instance.Title.ToString();
            float vanillaStep = __instance.Increment;
            float vanillaMin = __instance.ValidRange.min;
            float vanillaMax = __instance.ValidRange.max;

            // Widget isn't fully set up yet, leave it alone
            if (vanillaStep <= 0f || vanillaMax <= vanillaMin) return;

            // One log line per setting, so the exact names are easy to find in LogOutput.log
            if (Seen.Add(title))
            {
                Plugin.Logger.LogInfo(
                    $"Setting seen: {title} (vanilla step {vanillaStep}, min {vanillaMin}, max {vanillaMax}, format '{__instance.FormatString}')");
            }

            // Max players (4-15) stays vanilla
            if (vanillaMin == 4f && vanillaMax == 15f && vanillaStep == 1f) return;

            string section = Plugin.OverridePrefix + title;

            if (Plugin.Cfg.Bind(section, "Skip", false,
                    "Set to true to leave this one setting at its stock vanilla step and range.").Value)
                return;

            string stepOverride = Plugin.Cfg.Bind(section, "Step", "",
                "Leave blank to use the global rule. Type a number to force the step size for this setting.").Value;
            string minOverride = Plugin.Cfg.Bind(section, "Min", "",
                "Leave blank to use the global rule. Type a number to force the minimum for this setting.").Value;
            string maxOverride = Plugin.Cfg.Bind(section, "Max", "",
                "Leave blank to use the global rule. Type a number to force the maximum for this setting.").Value;

            bool decimalSetting = vanillaStep < 1f;

            float step = Math.Min(vanillaStep, decimalSetting ? Plugin.DecimalStep.Value : Plugin.WholeStep.Value);
            float min = Math.Min(vanillaMin, decimalSetting ? Plugin.MinDecimal.Value : Plugin.MinWhole.Value);
            float max = vanillaMax * Math.Max(1f, Plugin.MaxMultiplier.Value);

            if (TryParse(stepOverride, out float s) && s > 0f) step = s;
            if (TryParse(minOverride, out float lo)) min = lo;
            if (TryParse(maxOverride, out float hi)) max = hi;

            if (max <= min) return;

            __instance.Increment = step;
            __instance.ValidRange = new FloatRange(min, max);

            // Finer steps need a second decimal to be readable (0.55 instead of 0.6)
            if (decimalSetting && step < 0.1f)
            {
                string format = __instance.FormatString;
                if (!string.IsNullOrEmpty(format) && format.Contains(":0.0}"))
                    __instance.FormatString = format.Replace(":0.0}", ":0.00}");
            }
        }
        catch (Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogError($"Setting unlock failed for one setting, it stays at vanilla: {e}");
        }
    }

    private static bool TryParse(string text, out float value)
    {
        value = 0f;
        return !string.IsNullOrWhiteSpace(text)
               && float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
