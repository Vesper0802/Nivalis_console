using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;

namespace NivalisDevUnlock;

/// <summary>
/// Finds food that has gone off, in your pockets and in every venue you own, and bins it. Spoiled
/// stock is worth nothing but still occupies the storage a menu needs, and the game converts some
/// of it into RottenFood, which no screen will clear in bulk.
/// </summary>
internal static class Spoiled
{
    /// <summary>One container's worth of findings, kept so the report and the removal agree.</summary>
    private sealed class Haul
    {
        public readonly string Where;
        public readonly ItemContainer Container;
        public readonly List<(ItemStack Stack, ItemInstanceData Instance)> Items = new();
        public int Value;

        public Haul(string where, ItemContainer container) { Where = where; Container = container; }
    }

    public static void Run(string[] args, Action<string> print)
    {
        var mode = (args.Length > 0 ? args[0] : "list").ToLowerInvariant();
        if (mode != "list" && mode != "clear" && mode != "sell")
        {
            print("Usage: spoiled [clear|sell]. On its own it only reports, and touches nothing.");
            return;
        }

        var containers = Sources(args.Length > 1 ? string.Join(" ", args[1..]) : null, print);
        if (containers == null)
            return;

        // Every item type is probed by key rather than the container being enumerated, because
        // interop's generic collection enumerators are the one part of this API that has taken
        // the process down.
        var types = GameRefs.ItemTypes();
        var hauls = containers.Select(c => Scan(c.Where, c.Container, types)).ToList();
        var total = hauls.Sum(h => h.Items.Count);

        if (total == 0)
        {
            print("Nothing has gone off. Your inventory and every venue you own are clean.");
            return;
        }

        foreach (var haul in hauls.Where(h => h.Items.Count > 0))
            print($"  {haul.Where}: {haul.Items.Count} spoiled items worth {haul.Value}, " +
                  $"{Kinds(haul)}.");

        if (mode == "list")
        {
            print($"{total} spoiled items across {hauls.Count(h => h.Items.Count > 0)} places. " +
                  "Run 'spoiled clear' to bin it, or 'spoiled sell' to be paid for it.");
            return;
        }

        var removed = 0;
        var earned = 0;
        foreach (var haul in hauls.Where(h => h.Items.Count > 0))
        {
            var (took, worth) = Remove(haul, print);
            removed += took;
            earned += worth;
        }

        if (mode == "sell" && earned > 0)
        {
            // Nothing in the game buys spoiled food, so this is the plugin paying out its value
            // rather than a trade being made.
            var inventory = GameRefs.PlayerInventory;
            if (inventory == null)
                print("Could not pay out: no PlayerInventory in the scene.");
            else
            {
                try
                {
                    inventory.ReceiveMoney(earned);
                    print($"Binned {removed} spoiled items and paid out {earned} cents. " +
                          $"Money: {inventory.Money}.");
                    return;
                }
                catch (Exception e) { print($"Binned the items but could not pay out: {e.Message}"); }
            }
        }

        print($"Binned {removed} spoiled items" +
              (mode == "sell" ? " — they were worth nothing." : $" worth {earned} cents."));
    }

    /// <summary>
    /// Your pockets plus both containers of every venue you own. The player inventory is always
    /// included: a venue filter narrows which venues are visited, not whether you are.
    /// </summary>
    private static List<Haul> Sources(string filter, Action<string> print)
    {
        var list = new List<Haul>();

        var inventory = GameRefs.PlayerInventory;
        if (inventory == null)
        {
            print("No PlayerInventory in the scene — load a save first.");
            return null;
        }

        try
        {
            if (inventory.Items != null)
                list.Add(new Haul("your inventory", inventory.Items));
        }
        catch (Exception e) { print($"Could not read your inventory: {e.Message}"); }

        var player = GameRefs.Find<PlayerManager>()?.LocalPlayer;
        var manager = GameRefs.Find<VenueManager>();
        if (player == null || manager == null)
            return list;

        var venues = VenueRestock.Owned(player, filter, print);
        if (venues == null)
            return list;

        foreach (var venue in venues)
        {
            VenueAreaGhost ghost;
            try { ghost = manager.GetRuntimeData(venue); }
            catch { continue; }
            if (ghost == null)
                continue;

            try
            {
                if (ghost.FridgeInventory != null)
                    list.Add(new Haul($"{venue.name} fridge", ghost.FridgeInventory));
                if (ghost.CupboardInventory != null)
                    list.Add(new Haul($"{venue.name} cupboard", ghost.CupboardInventory));
            }
            catch (Exception e)
            {
                Plugin.Instance.Log.LogWarning($"spoiled: {venue.name}: {e.Message}");
            }
        }

        return list;
    }

    private static Haul Scan(string where, ItemContainer container, ItemType[] types)
    {
        var haul = new Haul(where, container);

        foreach (var type in types)
        {
            ItemStack stack;
            try { stack = container.GetStack(type); }
            catch { continue; }
            if (stack == null)
                continue;

            List<ItemInstanceData> instances;
            try { instances = Instances(stack); }
            catch { continue; }

            var rotten = IsRotten(type);
            foreach (var instance in instances)
            {
                if (!rotten && !IsSpoiled(instance))
                    continue;
                haul.Items.Add((stack, instance));
                haul.Value += Int(() => instance.Value);
            }
        }

        return haul;
    }

    private static (int, int) Remove(Haul haul, Action<string> print)
    {
        var before = Int(() => haul.Container.ItemCount);
        var took = 0;
        var worth = 0;
        var failed = 0;

        foreach (var (stack, instance) in haul.Items)
        {
            var value = Int(() => instance.Value);
            try
            {
                if (haul.Container.TryTake(stack, instance)) { took++; worth += value; }
                else failed++;
            }
            catch { failed++; }
        }

        // The container keeps its own counts, so it is told to recount rather than trusted to
        // have followed along, and the reading is reported either way.
        try { haul.Container.Refresh(); } catch { }
        var after = Int(() => haul.Container.ItemCount);

        if (failed > 0)
            print($"  {haul.Where}: {failed} of {haul.Items.Count} would not come out; " +
                  $"holding {before} -> {after}.");

        return (took, worth);
    }

    /// <summary>
    /// The backing list is indexed rather than iterated, for the same reason the container is
    /// probed by key. A copy is taken because the instances are about to be removed.
    /// </summary>
    private static List<ItemInstanceData> Instances(ItemStack stack)
    {
        var result = new List<ItemInstanceData>();
        var backing = stack._instanceData;
        if (backing == null)
            return result;
        for (var i = 0; i < backing.Count; i++)
            if (backing[i] != null)
                result.Add(backing[i]);
        return result;
    }

    private static bool IsSpoiled(ItemInstanceData instance)
    {
        try { return instance.Freshness == FoodFreshness.Spoiled; }
        catch { return false; }
    }

    /// <summary>
    /// What spoiled ingredients become once the game has processed them, so it is caught by type
    /// rather than by freshness — rot does not itself go off.
    /// </summary>
    private static bool IsRotten(ItemType type)
    {
        try { return GameRefs.AssetName(type) == "RottenFood"; }
        catch { return false; }
    }

    private static string Kinds(Haul haul)
    {
        var names = haul.Items
            .Select(i => { try { return GameRefs.DisplayName(i.Stack.Type); } catch { return "?"; } })
            .Distinct()
            .ToList();

        return names.Count <= 4
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(4)) + $" and {names.Count - 4} more";
    }

    private static int Int(Func<int> read)
    {
        try { return read(); }
        catch { return 0; }
    }
}
