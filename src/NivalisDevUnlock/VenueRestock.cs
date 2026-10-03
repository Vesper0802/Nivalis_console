using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;

namespace NivalisDevUnlock;

/// <summary>
/// Puts what each owned venue's menu is short of straight into its fridge and cupboard, which
/// is otherwise a per-venue manual transfer after every shopping trip.
/// </summary>
internal static class VenueRestock
{
    public static void Run(string filter, Action<string> print)
    {
        var log = Plugin.Instance.Log;

        var player = GameRefs.Find<PlayerManager>()?.LocalPlayer;
        var manager = GameRefs.Find<VenueManager>();
        if (player == null || manager == null)
        {
            print("No player or VenueManager in the scene — load a save first.");
            return;
        }

        var venues = Owned(player, filter, print);
        if (venues == null || venues.Count == 0)
            return;

        // The dictionary is probed by key rather than enumerated, because interop's generic
        // collection enumerators are the one part of this API that has taken the process down.
        var ingredients = new List<ItemType>();
        foreach (var item in GameRefs.ItemTypes())
        {
            try
            {
                if (item.IsIngredient || item.IsSeed || item.IsPlant)
                    ingredients.Add(item);
            }
            catch (Exception e)
            {
                log.LogWarning($"restock: could not read flags on an item type: {e.Message}");
            }
        }

        // These two are static settings on the manager, not per-venue state.
        print($"Restocking {venues.Count} venues. The game stocks " +
              $"{N(() => ShoppingListManager.demandAmount)} of each ingredient and flags a venue " +
              $"below {N(() => ShoppingListManager.itemThreshold)}.");
        log.LogInfo($"restock: {venues.Count} venues against {ingredients.Count} ingredient types");

        var totalItems = 0;
        var totalKinds = 0;
        var skipped = 0;

        foreach (var venue in venues)
        {
            VenueAreaGhost ghost;
            try { ghost = manager.GetRuntimeData(venue); }
            catch (Exception e)
            {
                log.LogWarning($"restock: no runtime data for {venue.name}: {e.Message}");
                skipped++;
                continue;
            }

            if (ghost == null)
            {
                print($"  {venue.name}: not loaded, skipped.");
                skipped++;
                continue;
            }

            Il2CppSystem.Collections.Generic.Dictionary<ItemType, ShoppingListManager.ShoppingListData> demand;
            try { demand = venue.GetIngredientsDemand(); }
            catch (Exception e)
            {
                log.LogWarning($"restock: GetIngredientsDemand failed for {venue.name}: {e.Message}");
                skipped++;
                continue;
            }

            if (demand == null)
            {
                skipped++;
                continue;
            }

            var cold = new List<ItemTypeAmount>();
            var dry = new List<ItemTypeAmount>();
            var target = 0;
            var kinds = 0;

            foreach (var item in ingredients)
            {
                int need;
                try
                {
                    if (!demand.ContainsKey(item))
                        continue;

                    var data = demand[item];
                    need = data?.MissingItems ?? 0;

                    // The menu's full appetite, not just today's shortfall, which is what
                    // decides whether a venue's storage can ever hold its menu.
                    target += data?.demand ?? 0;
                    kinds++;
                }
                catch (Exception e)
                {
                    log.LogWarning($"restock: could not read demand for {item.name}: {e.Message}");
                    continue;
                }

                if (need <= 0)
                    continue;

                var refrigerated = false;
                try { refrigerated = item.RequiresRefridgeration; }
                catch { }

                (refrigerated ? cold : dry).Add(new ItemTypeAmount(item, need));
            }

            // The demand list only carries ingredients that have fallen below the threshold, so
            // this is today's shopping list rather than the menu's full appetite.
            var storage = Capacity(ghost.FridgeInventory) + Capacity(ghost.CupboardInventory);
            if (kinds > 0)
                print($"  {venue.name}: short {target} units across {kinds} kinds, " +
                      $"storage holds {(storage < 0 ? "unlimited" : storage.ToString())}.");

            if (cold.Count == 0 && dry.Count == 0)
            {
                print($"  {venue.name}: already stocked.");
                continue;
            }

            var added = Fill(ghost.FridgeInventory, cold, "fridge", venue.name, print) +
                        Fill(ghost.CupboardInventory, dry, "cupboard", venue.name, print);

            totalItems += added;
            totalKinds += cold.Count + dry.Count;
            print($"  {venue.name}: +{added} items for the {cold.Count + dry.Count} kinds it was short " +
                  $"({cold.Count} chilled, {dry.Count} dry)");
        }

        print($"Restocked {venues.Count - skipped} venues: {totalItems} items, {totalKinds} entries." +
              (skipped > 0 ? $" {skipped} skipped — see the log." : ""));
    }

    /// <summary>
    /// This TryAdd overload is all or nothing: hand it more than fits and it stores none of it.
    /// So each kind goes in on its own call, trimmed to the room left, which keeps one oversized
    /// ingredient from costing the venue everything else it asked for. TryAdd reports nothing
    /// about what it took either, so the container is measured on both sides of the call.
    /// </summary>
    private static int Fill(ItemContainer container, List<ItemTypeAmount> items, string label,
        string venueName, Action<string> print)
    {
        if (container == null || items.Count == 0)
        {
            if (items.Count > 0)
                print($"  {venueName}: no {label} to put {items.Count} kinds into.");
            return 0;
        }

        var wanted = items.Sum(i => i.amount);
        var added = 0;
        var short_of = new List<ItemTypeAmount>();

        foreach (var item in items)
        {
            var room = Room(container);
            if (room == 0)
            {
                short_of.Add(item);
                continue;
            }

            var amount = room < 0 ? item.amount : Math.Min(item.amount, room);
            var before = Count(container);

            try
            {
                container.TryAdd(new Il2CppReferenceArray<ItemTypeAmount>(
                    new[] { new ItemTypeAmount(item.type, amount) }));
            }
            catch (Exception e)
            {
                Plugin.Instance.Log.LogWarning(
                    $"restock: {label} TryAdd failed for {Name(item.type)} at {venueName}: {e.Message}");
                short_of.Add(item);
                continue;
            }

            var took = Count(container) - before;
            added += took;
            if (took < item.amount)
                short_of.Add(new ItemTypeAmount(item.type, item.amount - took));
        }

        if (added < wanted)
            print($"  {venueName}: {label} took {added} of {wanted}, still short {Kinds(short_of)} — " +
                  $"{State(container)}.");

        return added;
    }

    /// <summary>
    /// How many more items the container will hold, or -1 when it reports no limit. Only the
    /// normal capacity is consulted: every venue container reads back a refrigerated capacity of
    /// zero while happily storing chilled goods, so that figure is not the limit in play.
    /// </summary>
    private static int Room(ItemContainer container)
    {
        try
        {
            var capacity = container.NormalCapacity;
            if (!capacity.HasValue)
                return -1;

            return Math.Max(capacity.Value - container.ItemCount, 0);
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"restock: could not read capacity: {e.Message}");
            return -1;
        }
    }

    private static int Count(ItemContainer container)
    {
        try { return container.ItemCount; }
        catch { return 0; }
    }

    /// <summary>How much the container holds in total, or -1 when it reports no limit.</summary>
    private static int Capacity(ItemContainer container)
    {
        try
        {
            var capacity = container.NormalCapacity;
            return capacity.HasValue ? capacity.Value : -1;
        }
        catch { return -1; }
    }

    private static string N(Func<int?> read)
    {
        try { return read()?.ToString() ?? "?"; }
        catch { return "?"; }
    }

    /// <summary>
    /// What the container says about itself, so a shortfall can be told apart from a command that
    /// simply failed. A capacity of null is the game's way of saying unlimited.
    /// </summary>
    private static string State(ItemContainer container)
    {
        try
        {
            return $"holding {container.ItemCount} of {Cap(container.NormalCapacity)} " +
                   $"in {container.StackCount} stacks";
        }
        catch (Exception e)
        {
            return $"could not read its capacity ({e.Message})";
        }
    }

    private static string Cap(Il2CppSystem.Nullable<int> capacity)
    {
        try { return capacity.HasValue ? capacity.Value.ToString() : "unlimited"; }
        catch { return "?"; }
    }

    /// <summary>Names the items, because knowing which one will not fit is the point.</summary>
    private static string Kinds(List<ItemTypeAmount> items)
    {
        var names = items.Select(i => Name(i.type)).ToList();
        return names.Count <= 4
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(4))} and {names.Count - 4} more";
    }

    private static string Name(ItemType type)
    {
        try { return type.name; }
        catch { return "?"; }
    }

    private static List<Venue> Owned(PlayerManager.Player player, string filter, Action<string> print)
    {
        var owned = new List<Venue>();
        try
        {
            var results = new Il2CppSystem.Collections.Generic.List<Venue>();
            player.GetOwnedVenues(results);
            for (var i = 0; i < results.Count; i++)
                if (results[i] != null)
                    owned.Add(results[i]);
        }
        catch (Exception e)
        {
            print($"Could not list your venues: {e.Message}");
            return null;
        }

        if (owned.Count == 0)
        {
            print("You do not own any venues. Use 'venues' to check.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(filter))
            return owned.OrderBy(v => v.name, StringComparer.OrdinalIgnoreCase).ToList();

        var matches = owned
            .Where(v => v.name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            print($"No venue of yours matches '{filter}'. Use 'venues' for the list.");
            return null;
        }

        return matches;
    }
}
