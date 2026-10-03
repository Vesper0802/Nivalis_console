using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis.Dialogue;
using Nivalis.InventorySystem;

namespace NivalisDevUnlock;

/// <summary>
/// The items the story is watching. ArticyGlobalInventoryLinker keeps a table of Articy variable
/// names against item types and listens to the player inventory, so taking one of these items
/// away also clears the variable a quest checks. Nothing on ItemType marks an item as a quest
/// item, which makes that table the only way to tell.
/// </summary>
internal static class StoryItems
{
    /// <summary>
    /// The linked item types keyed by guid, or null when the table cannot be read — which is the
    /// difference between "nothing is protected" and "we do not know what is protected", and the
    /// caller must not treat the second as the first.
    ///
    /// The key is the guid rather than the ItemType itself. Interop hands out a fresh managed
    /// wrapper for every access to a native object, so two wrappers for the same item compare as
    /// different references and a dictionary keyed on them never matches. Keying on the item
    /// silently protected nothing.
    /// </summary>
    public static Dictionary<string, (ItemType Type, string Variables)> Links(Action<string> print)
    {
        var linker = GameRefs.Find<ArticyGlobalInventoryLinker>();
        if (linker == null)
        {
            print("No ArticyGlobalInventoryLinker in the scene, so the story's item list cannot " +
                  "be read — load a save first.");
            return null;
        }

        var map = new Dictionary<string, (ItemType, string)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pairs = linker.Links;
            if (pairs == null)
                return map;

            for (var i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i];
                if (pair?.ItemType == null)
                    continue;

                var key = Key(pair.ItemType);
                if (key == null)
                    continue;

                var name = pair.VariableName ?? "?";
                // Several variables can point at one item, so the names are gathered rather
                // than the last one winning.
                map[key] = map.TryGetValue(key, out var seen)
                    ? (seen.Item1, $"{seen.Item2}, {name}")
                    : (pair.ItemType, name);
            }
        }
        catch (Exception e)
        {
            print($"Could not read the story's item list: {e.Message}");
            return null;
        }

        return map;
    }

    public static void Run(Action<string> print)
    {
        var links = Links(print);
        if (links == null)
            return;

        if (links.Count == 0)
        {
            print("The story is not watching any item in this save.");
            return;
        }

        var inventory = GameRefs.PlayerInventory;
        var held = new List<string>();
        var rest = 0;

        foreach (var (type, variables) in links.Values.OrderBy(v => GameRefs.AssetName(v.Type),
                     StringComparer.OrdinalIgnoreCase))
        {
            var count = 0;
            try { count = inventory?.Items == null ? 0 : inventory.Items.GetItemCount(type); }
            catch { }

            if (count > 0)
                held.Add($"  {count,4}x {GameRefs.AssetName(type)} " +
                         $"[{GameRefs.DisplayName(type)}] <- {variables}");
            else
                rest++;
        }

        print($"The story watches {links.Count} item types through Articy variables. " +
              "Removing one of these also clears the variable a quest reads.");

        if (held.Count == 0)
            print("You are not carrying any of them.");
        else
        {
            print($"You are carrying {held.Count} of them:");
            foreach (var line in held)
                print(line);
        }

        if (rest > 0)
            print($"The other {rest} are not in your inventory. 'clearitems' keeps all of them; " +
                  "'clearitems all' does not.");
    }

    /// <summary>
    /// Empties the inventory except for what the story is watching. Each type is taken out by
    /// itself rather than the container being cleared wholesale, which is what makes keeping
    /// anything back possible at all.
    /// </summary>
    public static void Clear(bool everything, Action<string> print)
    {
        var inventory = GameRefs.PlayerInventory;
        if (inventory?.Items == null)
        {
            print("No PlayerInventory in the scene — load a save first.");
            return;
        }

        if (everything)
        {
            var doomed = Protected(inventory.Items, print, out var unknown);
            if (doomed.Count > 0)
                print($"Clearing {doomed.Count} story items too: {string.Join(", ", doomed)}. " +
                      "The quests that read them will see them as gone.");
            else if (!unknown)
                print("No story items to lose.");

            var before = Count(inventory.Items);
            inventory.ClearItems();
            print($"Inventory cleared: {before} items gone.");
            return;
        }

        var links = Links(print);
        if (links == null)
        {
            print("Nothing was cleared. Run 'clearitems all' to clear it anyway.");
            return;
        }

        var removed = 0;
        var kept = new List<string>();
        var protectedTypes = new List<ItemType>();

        foreach (var type in GameRefs.ItemTypes())
        {
            try
            {
                if (!inventory.Items.HasItem(type))
                    continue;

                var count = inventory.Items.GetItemCount(type);

                // An item that cannot be identified is kept rather than cleared: being unable
                // to tell whether the story wants it is not a reason to destroy it.
                var key = Key(type);
                if (key == null || links.ContainsKey(key))
                {
                    kept.Add($"{count}x {GameRefs.DisplayName(type)}");
                    protectedTypes.Add(type);
                    continue;
                }

                if (inventory.Items.TakeAllByType(type))
                    removed += count;
            }
            catch (Exception e)
            {
                Plugin.Instance.Log.LogWarning(
                    $"clearitems: {GameRefs.AssetName(type)}: {e.Message}");
            }
        }

        print($"Cleared {removed} items, {Count(inventory.Items)} left.");

        if (kept.Count == 0)
        {
            print("You were not carrying anything the story watches.");
            return;
        }

        // The protection is checked against the inventory afterwards rather than reported on
        // trust. The first version of this matched item types by reference, kept nothing, and
        // said it had — so what survived is read back from the game.
        var lost = protectedTypes
            .Where(t => { try { return inventory.Items.GetItemCount(t) == 0; } catch { return true; } })
            .Select(t => GameRefs.DisplayName(t))
            .ToList();

        if (lost.Count > 0)
        {
            print($"WARNING: {lost.Count} items meant to be kept are gone: {string.Join(", ", lost)}. " +
                  "Give them back before saving — the story variable follows the item.");
            return;
        }

        print($"Kept {kept.Count} story items, all still in your bag: {string.Join(", ", kept)}.");
    }

    /// <summary>The story items currently held, for warning about before they are destroyed.</summary>
    private static List<string> Protected(ItemContainer container, Action<string> print,
        out bool unknown)
    {
        var quiet = new Action<string>(_ => { });
        var links = Links(quiet);
        unknown = links == null;
        if (links == null)
        {
            print("The story's item list could not be read, so what this destroys is unknown.");
            return new List<string>();
        }

        var held = new List<string>();
        foreach (var (type, _) in links.Values)
        {
            try
            {
                var count = container.GetItemCount(type);
                if (count > 0)
                    held.Add($"{count}x {GameRefs.DisplayName(type)}");
            }
            catch { }
        }

        return held;
    }

    /// <summary>
    /// A stable identity for an item type. Interop wrappers for the same native object are
    /// different references, so the item's own guid is what gets compared; the asset name stands
    /// in for anything whose guid will not read, and a null means it cannot be identified at all.
    /// </summary>
    private static string Key(ItemType type)
    {
        try
        {
            var guid = GameRefs.Guid(type);
            if (!string.IsNullOrWhiteSpace(guid))
                return guid;
        }
        catch { }

        try
        {
            var name = GameRefs.AssetName(type);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    private static int Count(ItemContainer container)
    {
        try { return container.ItemCount; }
        catch { return 0; }
    }
}
