using System;
using AmongUs.GameOptions;

namespace TaskDoorTweaks.Patches;

// Host-authoritative, Polus only (per the request this mod was built for — MIRA HQ has its
// own decontamination doors on the same DeconSystem class, but this mod leaves those alone).
//
// Every field/member touched here (DeconSystem.UpdateSystem, DoorOpenTime, DeconTime,
// GameOptionsManager.Instance.currentNormalGameOptions, MapId, MapNames)
// is confirmed straight from ApeMV/AmongUsRevamped's own working DeconSystemPatch and Main.cs,
// not guessed from the NuGet reference package.
//
// Everything in the prefix is wrapped in a try/catch on purpose. A prefix that throws stops
// the game's own UpdateSystem from running at all, and for the decon doors that means they
// click but never open. With the catch, the worst a failure here can do is leave the door
// timing at vanilla — the doors themselves always keep working.
[HarmonyPatch(typeof(DeconSystem), nameof(DeconSystem.UpdateSystem))]
internal static class DeconDoorPatch
{
    private static bool _errorLogged;
    private static bool _appliedLogged;

    public static void Prefix(DeconSystem __instance)
    {
        try
        {
            if (!AmongUsClient.Instance.AmHost) return;

            // 'var' on purpose: the exact options class name (NormalGameOptionsV11, V12, ...)
            // changes between game versions, and this code never needs to name it.
            var normalOptions = GameOptionsManager.Instance?.currentNormalGameOptions;
            if (normalOptions == null) return;

            MapNames currentMap = (MapNames)normalOptions.MapId;
            if (currentMap != MapNames.Polus) return;

            float seconds = Plugin.PolusDeconSeconds.Value;
            __instance.DoorOpenTime = seconds;
            __instance.DeconTime = seconds;

            if (!_appliedLogged)
            {
                _appliedLogged = true;
                Plugin.Logger.LogInfo($"Polus decon doors set to {seconds}s (door open time and decon time).");
            }
        }
        catch (Exception e)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Plugin.Logger.LogError($"Decon door patch failed, doors left at vanilla timing: {e}");
        }
    }
}
