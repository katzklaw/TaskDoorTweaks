# TaskDoorTweaks

A small standalone BepInEx/IL2CPP mod. Three independent features:

1. Lets you disable any task type so it's never assigned — **Medbay "Submit Scan" is disabled by default**, since that's what this mod was originally built for. Every other task type is also exposed as its own toggle in case that's handy later, but they all default to *enabled* (unchanged from vanilla).
2. Lets you set how many seconds the **Polus** decontamination doors take to cycle, from 0.5 to 6 (any decimal, not just halves). MIRA HQ's decon doors are left untouched.
3. Lets you **unlock** every vanilla numeric host setting — step size (increment) and min/max range — instead of being stuck with vanilla's fixed values. This covers things like vision (crew/impostor light radius, flashlight size), kill cooldown, common/short/long task counts, number of impostors, voting/discussion/emergency time — anything shown as a +/- stepper in the host settings menu.

Each feature is its own patch file, applied independently with its own try/catch (see `Plugin.PatchResilient`), following the same pattern as your other standalone mods (BanListMod, Ender, TaskKillTracker): no shared project references, no in-game menu of its own, nothing bundled in that could break and take the rest down with it.

## Config

No custom in-game UI — everything lives in the auto-generated BepInEx config file at:

```
BepInEx/config/com.katzklaw.taskdoortweaks.cfg
```

It's created the first time the plugin loads, and grows as you use the game's own menus. Sections:

- `[Polus Decontamination Doors]` → `DoorCycleSeconds` (0.5-6, default 3 = vanilla; accepts any decimal, not just halves). Applied to **both** `DoorOpenTime` and `DeconTime` on `DeconSystem`, so a full door-open → decontaminate → door-close round trip is double whatever you set — `0.5` gives the fastest possible round trip, 1 second total. Vanilla's default of 3+3 is presumably where "6 seconds" comes from — worth confirming in-game, since that number was inferred from how `AmongUsRevamped` resets these fields when its own version of this toggle is off, not from a direct read of the base game's default. This is a plain config value, not an in-game slider, so there's no real concept of a "step size" here the way there is for feature 3 below — you just type the number you want.
- `[Tasks]` → one `Disable_<TaskType>` bool per task type (e.g. `Disable_SubmitScan = true`). Set any of them to `true` to stop that task from ever being handed out. `Disable_SubmitScan` ships as `true`; everything else ships `false`.
- `[Setting Unlock]` → `Enabled` (default `true`), a master on/off switch for feature 3.
- Also in `[Setting Unlock]`: `DecimalStep` (default `0.05`, for settings vanilla steps by less than 1, like speed and vision), `WholeStep` (default `1`), `MinDecimal` (`0.05`), `MinWhole` (`0`), and `MaxMultiplier` (`2`, new max = vanilla max x this). With `Enabled` on these apply to every setting automatically, no editing needed. Max players (4-15) is never changed.
- Optional: one `[Unlock Override - <setting name>]` section per setting, created the first time that setting is seen when you open the host settings menu, with `Skip`, `Step`, `Min`, `Max`. BepInEx sorts sections alphabetically, so these sit at the very bottom of the file. (Before v1.3.1 they were called `[Override - <setting name>]`; any values you had typed into those are carried over automatically.)
- On startup the plugin also removes leftover sections from older versions that nothing reads any more (the old `[GameKillCooldown]`-style Step/Min/Max sections, `[CapacityLabel]`, etc.) and any override sections you never changed. Leave `Step`/`Min`/`Max` blank to use the global rules, type a number to force one, or set `Skip` to true to keep that one setting at vanilla. Every setting seen is also logged once to the BepInEx log as `Setting seen: <name> ...`.

Only the **host** needs this mod installed for features 1 and 2 (they're host-authoritative and sync to everyone). Feature 3 only changes what the host is *able to select* in their own menu — once selected, the value is sent to everyone the normal way, so again, only the host needs it installed.

## Building

Requires the .NET 6 SDK. From this folder:

```
dotnet build -c Release
```

Optionally pass `-p:AmongUs="C:\path\to\Among Us"` (or set an `AmongUs` environment variable) and the build will automatically copy the built DLL into `BepInEx/plugins/` for you, same convention as your other mods.

Drop `TaskDoorTweaks.dll` into `BepInEx/plugins/` on the **host's** game install if you build it manually instead.

## Why these APIs

Everything this mod touches (`ShipStatus.AddTasksFromList`, `NormalPlayerTask.TaskType`, `DeconSystem.UpdateSystem`, `DeconSystem.DoorOpenTime`/`DeconTime`, `GameOptionsManager.Instance.currentNormalGameOptions.MapId` cast to `MapNames`, `NumberOption.SetUpFromData`/`.Increment`/`.ValidRange`) was verified directly against `ApeMV/AmongUsRevamped`'s own source rather than trusted from the `AmongUs.GameLibs.Steam` NuGet package alone, per the known runtime-vs-compile-time mismatch risk with that package. The settings-unlock approach specifically mirrors `AmongUsRevamped`'s `Patches/NumberOptionsPatch.cs`, which is itself explicitly credited there to the well-known `astra1dev/AUnlocker` mod — this is a widely-used, proven technique, not something invented for this mod. If a future Among Us update renames or removes any of these members, each patch is applied independently so a break in one doesn't take out the others.

## Testing notes

Not yet build-tested against a live game client in this session (no .NET SDK / game assemblies available where it was built) — please run it through your usual build → four-client test loop and report back anything the BepInEx console flags.

One thing worth watching for specifically with feature 3: pushing a setting far outside its vanilla range (e.g. impostor count, which is tied to lobby size elsewhere in the game) may be accepted by the menu widget but still get rejected — or clamped back — by the game's own logic when the match actually starts. That would be the base game's own validation kicking in, not a bug in this mod, but it's the one place where "unlocked" doesn't necessarily mean "guaranteed to work end to end" — worth a quick sanity check in your test loop.
