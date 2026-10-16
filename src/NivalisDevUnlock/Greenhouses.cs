using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;
using UnityEngine;

namespace NivalisDevUnlock;

/// <summary>
/// Reports what each greenhouse you own is growing and holding, and switches its modules between
/// tending themselves and waiting for you. Game update #5 gave greenhouses their own storage and
/// let the modules harvest and replant on their own, which put the state worth checking behind a
/// console you have to walk up to, one greenhouse at a time.
/// </summary>
internal static class Greenhouses
{
    public static void Run(string[] args, Action<string> print)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        if (mode == "auto")
        {
            Automate(args, print);
            return;
        }

        var filter = args.Length > 0 ? string.Join(" ", args) : null;
        var found = Owned(filter, print);
        if (found == null)
            return;

        var manager = GameRefs.Find<GreenhouseManager>();
        var player = GameRefs.Find<PlayerManager>()?.LocalPlayer;

        print($"{found.Count} greenhouses. {Unlocked(manager)}");

        foreach (var greenhouse in found)
        {
            var ghost = Area(manager, greenhouse);
            if (ghost == null)
            {
                print($"  {greenhouse.name}: not loaded, so it has nothing to report.");
                continue;
            }

            print($"  {greenhouse.name}: {Conditions(ghost)}");
            print($"    storage: {Storage(manager, greenhouse, player, ghost)}");
            Report(ghost, print);

            var upgrades = Upgrades(ghost);
            if (upgrades.Count > 0)
                print($"    upgrades: {string.Join(", ", upgrades)}");
        }
    }

    /// <summary>
    /// Turns self-tending on or off across every module. The game gates these behind the
    /// Automatic Harvester and RePlanter 4000 upgrades, which this does not check: the point of
    /// the console is to reach settings the save has not unlocked yet.
    /// </summary>
    private static void Automate(string[] args, Action<string> print)
    {
        if (args.Length < 2 || (args[1] != "on" && args[1] != "off"))
        {
            print("Usage: greenhouse auto <on|off> [name fragment].");
            return;
        }

        var on = args[1] == "on";
        var filter = args.Length > 2 ? string.Join(" ", args[2..]) : null;
        var found = Owned(filter, print);
        if (found == null)
            return;

        var manager = GameRefs.Find<GreenhouseManager>();
        var changed = 0;
        var modules = 0;

        foreach (var greenhouse in found)
        {
            var ghost = Area(manager, greenhouse);
            if (ghost == null)
                continue;

            foreach (var module in Modules(ghost))
            {
                modules++;
                try
                {
                    module.AutoHarvest = on;
                    module.AutoPlant = on;
                    changed++;
                }
                catch (Exception e)
                {
                    Plugin.Instance.Log.LogWarning(
                        $"greenhouse: could not set automation on a module of {greenhouse.name}: {e.Message}");
                }
            }
        }

        // The modules are read back rather than counted as set, because a write that the game
        // rejects or overwrites would otherwise be reported as a success.
        var now = 0;
        foreach (var greenhouse in found)
        {
            var ghost = Area(manager, greenhouse);
            if (ghost == null)
                continue;
            foreach (var module in Modules(ghost))
                if (Bool(() => module.AutoHarvest) == on && Bool(() => module.AutoPlant) == on)
                    now++;
        }

        print($"Automation {(on ? "on" : "off")} for {now} of {modules} modules across " +
              $"{found.Count} greenhouses.");
        if (now != changed)
            print($"  {changed - now} modules did not keep the setting — see the log.");
    }

    /// <summary>
    /// Condition control and the upgrade shop are gated behind farming levels, so a greenhouse
    /// can report a temperature that you have no way to change yet. 'skill farming' lists which
    /// level opens each one.
    /// </summary>
    private static string Unlocked(GreenhouseManager manager)
    {
        if (manager == null)
            return "No GreenhouseManager in the scene, so the farming unlocks are unknown.";

        var locked = new List<string>();
        try
        {
            if (!manager.IsConditionsSetupUnlocked())
                locked.Add("condition control");
            if (!manager.IsFarmingUpgradesUnlocked())
                locked.Add("greenhouse upgrades");
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"greenhouse: could not read the farming unlocks: {e.Message}");
            return "";
        }

        return locked.Count == 0
            ? "Your farming level has opened everything."
            : $"Your farming level has not opened {string.Join(" or ", locked)} yet.";
    }

    private static void Report(GreenhouseAreaGhost ghost, Action<string> print)
    {
        var modules = Modules(ghost);
        if (modules.Count == 0)
        {
            print("    modules: none placed.");
            return;
        }

        var idle = 0;
        var ready = 0;

        foreach (var module in modules)
        {
            var plant = Name(() => module.PlantType);
            var planted = Bool(() => module.Planted);
            if (!planted)
            {
                idle++;
                continue;
            }

            if (Bool(() => module.ReadyForHarvest))
                ready++;

            print($"      {Name(() => module.ModuleType)}: {plant} {Progress(module)}" +
                  $"{Yield(ghost, module)}{Auto(module)}");
        }

        print($"    modules: {modules.Count} placed, {ready} ready to harvest, {idle} empty.");
    }

    /// <summary>
    /// Growth is reported against the cost the module is working towards rather than as a bare
    /// number, but ReadyForHarvest is what the game itself answers, so it leads.
    /// </summary>
    private static string Progress(GreenhouseModuleGhost module)
    {
        if (Bool(() => module.ReadyForHarvest))
            return "ready";

        try
        {
            var grown = module.Growth;
            var cost = module.growthCost;
            return cost > 0
                ? $"{grown / cost * 100f:0}% grown"
                : $"{grown:0.##} grown";
        }
        catch { return "growth unknown"; }
    }

    private static string Yield(GreenhouseAreaGhost ghost, GreenhouseModuleGhost module)
    {
        try
        {
            var yield = ghost.CalculateExpectedYield(module);
            var speed = ghost.CalculateGrowthSpeedInPercents(module);
            return $", about {yield:0.#} per harvest at {speed}% speed";
        }
        catch { return ""; }
    }

    private static string Auto(GreenhouseModuleGhost module)
    {
        var harvest = Bool(() => module.AutoHarvest);
        var plant = Bool(() => module.AutoPlant);
        if (harvest && plant) return ", self-tending";
        if (harvest) return ", auto-harvest";
        if (plant) return ", auto-replant";
        return "";
    }

    private static string Conditions(GreenhouseAreaGhost ghost)
    {
        var parts = new List<string>();
        try { parts.Add($"{ghost.Temperature:0.#}\u00b0 / {ghost.Humidity:0.#}% humidity"); }
        catch { parts.Add("conditions unreadable"); }

        if (Bool(() => ghost.hasCompost))
            parts.Add("composted");
        parts.Add(Bool(() => ghost.IsGrowing) ? "growing" : "idle");

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Greenhouse storage is a VenueInventory, the same split pair a venue uses, so chilled and
    /// dry are reported apart for the same reason: a crop can only go in the one that suits it.
    /// Anything that fits in neither spills into the overflow container and then your pockets.
    /// </summary>
    private static string Storage(GreenhouseManager manager, Greenhouse greenhouse,
        PlayerManager.Player player, GreenhouseAreaGhost ghost)
    {
        var inventory = Inventory(manager, greenhouse, player);
        if (inventory == null)
            return "none reachable — place furniture with a storage value to give it some.";

        var parts = new List<string>();
        try
        {
            parts.Add($"{inventory.RefridgeratedCount}/{inventory.RefridgeratedCapacity} chilled");
            parts.Add($"{inventory.NormalCount}/{inventory.NormalCapacity} dry");
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"greenhouse: could not read storage counts: {e.Message}");
            return "counts unreadable — see the log.";
        }

        try
        {
            var baseDry = ghost.BaseNormalStorage;
            var baseCold = ghost.BaseFridgeStorage;
            if (baseDry > 0 || baseCold > 0)
                parts.Add($"{baseCold} chilled and {baseDry} dry of that is the building's own");
        }
        catch { }

        var overflow = Int(() => ghost.StorageOverflowContainer?.ItemCount ?? 0);
        if (overflow > 0)
            parts.Add($"{overflow} items have spilled into the overflow container");

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The storage hangs off the manager rather than the ghost, because it only exists for a
    /// greenhouse that is actually yours.
    /// </summary>
    internal static VenueInventory Inventory(GreenhouseManager manager, Greenhouse greenhouse,
        PlayerManager.Player player)
    {
        if (manager == null || greenhouse == null || player == null)
            return null;

        try
        {
            return manager.TryGetOwnedStorage(greenhouse, player, out var storage) ? storage : null;
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning(
                $"greenhouse: TryGetOwnedStorage failed for {greenhouse.name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Indexed rather than iterated, like every other interop collection the console reads: the
    /// generic enumerators are the one part of this API that has taken the process down.
    /// </summary>
    internal static List<GreenhouseModuleGhost> Modules(GreenhouseAreaGhost ghost)
    {
        var result = new List<GreenhouseModuleGhost>();
        try
        {
            var list = ghost.Modules;
            if (list == null)
                return result;

            for (var i = 0; i < list.Count; i++)
            {
                try
                {
                    var reference = list[i];
                    if (reference.TryGetValue(out var module) && module != null)
                        result.Add(module);
                }
                catch { }
            }
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"greenhouse: could not list modules: {e.Message}");
        }

        return result;
    }

    private static List<string> Upgrades(GreenhouseAreaGhost ghost)
    {
        var names = new List<string>();
        try
        {
            var list = ghost.upgrades;
            if (list == null)
                return names;

            for (var i = 0; i < list.Count; i++)
            {
                try
                {
                    var upgrade = list[i];
                    if (upgrade != null)
                        names.Add(((UnityEngine.Object)upgrade).name);
                }
                catch { }
            }
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"greenhouse: could not list upgrades: {e.Message}");
        }

        return names;
    }

    internal static GreenhouseAreaGhost Area(GreenhouseManager manager, Greenhouse greenhouse)
    {
        try { return manager?.GetArea(greenhouse); }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning(
                $"greenhouse: no runtime data for {greenhouse.name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The greenhouses you hold, by the same test the property commands use: the authored
    /// OwnershipType reads None even for one that is yours, so PlayerOwned is what decides.
    /// </summary>
    internal static List<Greenhouse> Owned(string filter, Action<string> print)
    {
        var all = new List<Greenhouse>();
        try
        {
            foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Greenhouse>()))
            {
                var greenhouse = obj?.TryCast<Greenhouse>();
                if (greenhouse == null)
                    continue;

                try
                {
                    if (greenhouse.PlayerOwned)
                        all.Add(greenhouse);
                }
                catch { }
            }
        }
        catch (Exception e)
        {
            print($"Could not list your greenhouses: {e.Message}");
            return null;
        }

        if (all.Count == 0)
        {
            print("You do not hold any greenhouses. Use 'properties' to see what is out there.");
            return null;
        }

        var ordered = all.OrderBy(g => g.name, StringComparer.OrdinalIgnoreCase).ToList();
        if (string.IsNullOrWhiteSpace(filter) || filter == "list")
            return ordered;

        var matches = ordered
            .Where(g => g.name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            print($"No greenhouse of yours matches '{filter}'. Run 'greenhouse' for the list.");
            return null;
        }

        return matches;
    }

    private static string Name(Func<ItemType> read)
    {
        try
        {
            var type = read();
            return type == null ? "empty" : GameRefs.DisplayName(type) is { Length: > 0 } name
                ? name
                : GameRefs.AssetName(type);
        }
        catch { return "?"; }
    }

    private static bool Bool(Func<bool> read)
    {
        try { return read(); }
        catch { return false; }
    }

    private static int Int(Func<int> read)
    {
        try { return read(); }
        catch { return 0; }
    }
}
