using BepInEx.Configuration;

namespace TaskDoorTweaks.Patches;

// Host-authoritative: this runs when the host builds the pool of tasks a player can be
// assigned from, and simply drops anything toggled off in the config before it's handed
// out. Only the host needs the mod installed for this to affect everyone in the lobby.
//
// Signature confirmed against ApeMV/AmongUsRevamped's own (working, currently-shipping)
// patch of the same method — only the 5th parameter (index 4, the "unusedTasks" list) is
// declared, via [HarmonyArgument(4)], which is all Harmony needs even though the real
// method has more parameters before it.
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.AddTasksFromList))]
internal static class TaskDisablePatch
{
    public static void Prefix([HarmonyArgument(4)] Il2CppSystem.Collections.Generic.List<NormalPlayerTask> unusedTasks)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        List<NormalPlayerTask> toRemove = new();

        foreach (NormalPlayerTask task in unusedTasks)
        {
            if (Plugin.DisableTask.TryGetValue(task.TaskType, out ConfigEntry<bool> entry) && entry.Value)
                toRemove.Add(task);
        }

        foreach (NormalPlayerTask task in toRemove)
        {
            Plugin.Logger.LogInfo($"Removing disabled task from pool: {task.TaskType}");
            unusedTasks.Remove(task);
        }
    }
}
