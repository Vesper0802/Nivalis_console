using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.InventorySystem;
using UnityEngine;

namespace NivalisDevUnlock;

/// <summary>
/// Locates the game objects the console operates on. Everything here can legitimately
/// return null: the player only exists once a save is actually loaded.
/// </summary>
internal static class GameRefs
{
    private static ItemType[] _itemCache;

    public static PlayerInventory PlayerInventory
    {
        get
        {
            // Generic FindObjectOfType<T>() is unreliable across interop, so go through
            // the Il2CppType overload and cast the result ourselves.
            var found = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<PlayerInventory>());
            return found == null ? null : found.TryCast<PlayerInventory>();
        }
    }

    public static ShoppingListManager ShoppingList => Find<ShoppingListManager>();

    /// <summary>
    /// Finds a live instance the same way as PlayerInventory. Used for the Singleton&lt;T&gt;
    /// managers, whose generic static Instance property does not survive interop cleanly.
    /// </summary>
    public static T Find<T>() where T : UnityEngine.Object
    {
        var found = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<T>());
        return found == null ? null : found.TryCast<T>();
    }

    /// <summary>
    /// Every ItemType currently loaded in memory. ItemTypes are ScriptableObjects, so this
    /// only sees the ones the game has already pulled in — it grows as you load areas.
    /// </summary>
    public static ItemType[] ItemTypes(bool refresh = false)
    {
        if (_itemCache != null && !refresh)
            return _itemCache;

        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ItemType>());
        var list = new List<ItemType>(all.Length);
        foreach (var obj in all)
        {
            var item = obj?.TryCast<ItemType>();
            if (item != null)
                list.Add(item);
        }

        list.Sort((a, b) => string.Compare(AssetName(a), AssetName(b), StringComparison.OrdinalIgnoreCase));
        _itemCache = list.ToArray();
        return _itemCache;
    }

    public static void InvalidateItemCache() => _itemCache = null;

    /// <summary>Asset name, which is stable and unlocalised — the best thing to match on.</summary>
    public static string AssetName(ItemType item)
    {
        try { return item.name ?? "<unnamed>"; }
        catch { return "<unnamed>"; }
    }

    /// <summary>Localised display name. Throws before localisation is ready, hence the guard.</summary>
    public static string DisplayName(ItemType item)
    {
        try { return item.Name ?? ""; }
        catch { return ""; }
    }

    /// <summary>The item's own identifier, which is what the game uses internally.</summary>
    public static string Guid(ItemType item)
    {
        try { return item.Guid ?? ""; }
        catch { return ""; }
    }

    public static string ArticyGuid(ItemType item)
    {
        try { return item.ArticyGuid ?? ""; }
        catch { return ""; }
    }

    /// <summary>Short form of the guid, enough to identify an item when typing.</summary>
    public static string ShortGuid(ItemType item)
    {
        var guid = Guid(item);
        return guid.Length <= 8 ? guid : guid[..8];
    }

    /// <summary>
    /// Resolves a user-typed query to item types. Exact matches win, in order of how
    /// unambiguous they are: guid, then asset name, then display name. Only if none hit
    /// does it fall back to substring matching, which may return several candidates.
    /// </summary>
    public static List<ItemType> Resolve(string query)
    {
        var items = ItemTypes();

        foreach (var item in items)
            if (string.Equals(Guid(item), query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ArticyGuid(item), query, StringComparison.OrdinalIgnoreCase))
                return new List<ItemType> { item };

        foreach (var item in items)
            if (string.Equals(AssetName(item), query, StringComparison.OrdinalIgnoreCase))
                return new List<ItemType> { item };

        foreach (var item in items)
            if (string.Equals(DisplayName(item), query, StringComparison.OrdinalIgnoreCase))
                return new List<ItemType> { item };

        // A guid prefix is precise enough to accept on its own if it hits exactly one item.
        var byGuidPrefix = items.Where(i =>
            Guid(i).StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byGuidPrefix.Count == 1)
            return byGuidPrefix;

        var partial = new List<ItemType>();
        foreach (var item in items)
        {
            if (AssetName(item).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                DisplayName(item).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                Guid(item).StartsWith(query, StringComparison.OrdinalIgnoreCase))
                partial.Add(item);
        }

        return partial;
    }
}
