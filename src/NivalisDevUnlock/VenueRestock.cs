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

        // How much of each ingredient a venue keeps is its own management setting, so the only
        // figure that is still global is the threshold for calling a venue low.
        print($"Restocking {venues.Count} venues. Each one stocks to its own supply target, " +
              $"shown below; the game flags an ingredient as low under " +
              $"{N(() => ShoppingListManager.itemThreshold)}.");
        log.LogInfo($"restock: {venues.Count} venues against {ingredients.Count} ingredient types");

        // Only used to report the tier's basis; the amounts restocked come from the menu's own
        // demand either way, so failing to bind costs a figure in the output and nothing else.
        var canReadTarget = Interop.HasNativeMethod(typeof(VenueAreaGhost), "GetSupplyTarget");
        if (!canReadTarget)
            log.LogWarning("restock: VenueAreaGhost.GetSupplyTarget did not bind; " +
                           "the per-ingredient basis will be left out of the output.");

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
            var lowBasis = int.MaxValue;
            var highBasis = 0;

            foreach (var item in ingredients)
            {
                int need;
                try
                {
                    if (!demand.ContainsKey(item))
                        continue;

                    // ShoppingListData is a struct, so a missing entry throws rather than
                    // reading back null; ContainsKey above is what keeps this safe.
                    var data = demand[item];
                    need = data.MissingItems;

                    // The menu's full appetite, not just today's shortfall, which is what
                    // decides whether a venue's storage can ever hold its menu.
                    target += data.demand;
                    kinds++;

                    // GetSupplyTarget is the venue's per-ingredient base for its tier, not the
                    // target for this ingredient: at Normal it reads 5 for everything, while the
                    // demand above is that base multiplied by how much the menu leans on the
                    // ingredient. Collected only to report the base, because under RecentSales it
                    // varies per ingredient and a single figure would misrepresent the venue.
                    if (canReadTarget)
                    {
                        var basis = ghost.GetSupplyTarget(item);
                        lowBasis = Math.Min(lowBasis, basis);
                        highBasis = Math.Max(highBasis, basis);
                    }
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

            // This is the menu's full appetite, not the shortfall: target sums every kind's
            // demand whether or not any is missing. The two numbers differ by a lot, so saying
            // "short" here would contradict the count of items actually added below.
            //
            // The two containers are reported separately because they do not pool: a chilled
            // ingredient can only go in the fridge, so a single total would suggest a flexibility
            // the venue does not have.
            if (kinds > 0)
                print($"  {venue.name}: {Setting(ghost, lowBasis, highBasis)} wants {target} units " +
                      $"across {kinds} kinds, storage holds {Shown(ghost.FridgeInventory)} chilled " +
                      $"and {Shown(ghost.CupboardInventory)} dry.");

            if (cold.Count == 0 && dry.Count == 0)
            {
                print($"  {venue.name}: already stocked.");
                continue;
            }

            var added = Fill(ghost.FridgeInventory, cold, "fridge", venue.name, print) +
                        Fill(ghost.CupboardInventory, dry, "cupboard", venue.name, print);

            totalItems += added;
            totalKinds += cold.Count + dry.Count;
            print($"  {venue.name}: +{added} items topping up {cold.Count + dry.Count} kinds that " +
                  $"had run low ({cold.Count} chilled, {dry.Count} dry)");
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
            var capacity = Limit(container.NormalCapacity);
            if (capacity < 0)
                return -1;

            return Math.Max(capacity - container.ItemCount, 0);
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
        try { return Limit(container.NormalCapacity); }
        catch { return -1; }
    }

    /// <summary>
    /// Whether the venue draws on your greenhouses before paying a vendor. A venue that can but
    /// does not is worth pointing out, since produce you grew is sitting unused; one that cannot
    /// has no greenhouse stocked with anything on its menu, which is not a setting to change.
    /// </summary>
    private static string Greenhouse(VenueAreaGhost ghost)
    {
        try
        {
            if (ghost.UseGreenhouseStock)
                return ", drawing on greenhouse stock";

            return ghost.CanUseGreenhouseStock
                ? ", not drawing on greenhouse stock though it could"
                : "";
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning(
                $"restock: could not read the greenhouse stock setting: {e.Message}");
            return "";
        }
    }

    private static string Shown(ItemContainer container)
    {
        var capacity = Capacity(container);
        return capacity < 0 ? "unlimited" : capacity.ToString();
    }

    /// <summary>
    /// Capacity used to be nullable, where null meant unlimited. It is now a plain count with no
    /// way left to say so, and a container that reports nothing is treated as unbounded rather
    /// than full: Fill measures the container on both sides of every add, so an optimistic limit
    /// costs a refused add that gets reported accurately, while a pessimistic one would stop
    /// restocking entirely.
    /// </summary>
    private static int Limit(int capacity) => capacity > 0 ? capacity : -1;

    /// <summary>
    /// Which management setting the venue is restocking to. Every figure comes straight off the
    /// venue rather than from any global, because Update #4 moved the target per venue. The basis
    /// is given as a range: the tiers other than RecentSales read the same for every ingredient,
    /// and collapsing a range that genuinely varies would hide that.
    /// </summary>
    private static string Setting(VenueAreaGhost ghost, int lowBasis, int highBasis)
    {
        var tier = "?";
        try { tier = ghost.SupplyTarget.ToString(); }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"restock: could not read the supply target: {e.Message}");
        }

        // The game shows what has been spent beside what was set, so both are reported rather
        // than the limit alone: a venue sitting at its budget is short for a different reason
        // than one whose menu outgrew its storage.
        var budget = "?";
        try { budget = $"{ghost.CurrentRestockSpending} of {ghost.DailyRestockBudget} spent"; }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"restock: could not read the restock budget: {e.Message}");
        }

        var basis = highBasis <= 0
            ? ""
            : lowBasis == highBasis
                ? $", {lowBasis} per ingredient use"
                : $", {lowBasis}-{highBasis} per ingredient use";

        return $"supply target {tier}{basis} (daily restock budget {budget}{Greenhouse(ghost)})";
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
            var capacity = Limit(container.NormalCapacity);
            return $"holding {container.ItemCount} of " +
                   $"{(capacity < 0 ? "unlimited" : capacity.ToString())} " +
                   $"in {container.StackCount} stacks";
        }
        catch (Exception e)
        {
            return $"could not read its capacity ({e.Message})";
        }
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

    internal static List<Venue> Owned(PlayerManager.Player player, string filter, Action<string> print)
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
