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
            foreach (var item in ingredients)
            {
                int need;
                try
                {
                    if (!demand.ContainsKey(item))
                        continue;
                    var data = demand[item];
                    need = data?.MissingItems ?? 0;
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

            if (cold.Count == 0 && dry.Count == 0)
            {
                print($"  {venue.name}: already stocked.");
                continue;
            }

            var added = Fill(ghost.FridgeInventory, cold, "fridge", venue.name, print) +
                        Fill(ghost.CupboardInventory, dry, "cupboard", venue.name, print);

            totalItems += added;
            totalKinds += cold.Count + dry.Count;
            print($"  {venue.name}: +{added} items across {cold.Count + dry.Count} kinds " +
                  $"({cold.Count} chilled, {dry.Count} dry)");
        }

        print($"Restocked {venues.Count - skipped} venues: {totalItems} items, {totalKinds} entries." +
              (skipped > 0 ? $" {skipped} skipped — see the log." : ""));
    }

    /// <summary>
    /// TryAdd reports nothing about what fitted, so the container is measured either side of the
    /// call; a shortfall means the venue is out of storage rather than that the command failed.
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
        var before = Count(container);

        try
        {
            container.TryAdd(new Il2CppReferenceArray<ItemTypeAmount>(items.ToArray()));
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"restock: {label} TryAdd failed for {venueName}: {e.Message}");
            return 0;
        }

        var added = Count(container) - before;
        if (added < wanted)
            print($"  {venueName}: {label} took {added} of {wanted} — out of space.");
        return added;
    }

    private static int Count(ItemContainer container)
    {
        try { return container.ItemCount; }
        catch { return 0; }
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
