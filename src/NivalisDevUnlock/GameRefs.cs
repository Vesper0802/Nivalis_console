using Il2CppInterop.Runtime;
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

    /// <summary>
    /// Resolves a user-typed query to item types: exact asset name first, then exact
    /// display name, then substring matches across both.
    /// </summary>
    public static List<ItemType> Resolve(string query)
    {
        var items = ItemTypes();

        foreach (var item in items)
            if (string.Equals(AssetName(item), query, StringComparison.OrdinalIgnoreCase))
                return new List<ItemType> { item };

        foreach (var item in items)
            if (string.Equals(DisplayName(item), query, StringComparison.OrdinalIgnoreCase))
                return new List<ItemType> { item };

        var partial = new List<ItemType>();
        foreach (var item in items)
        {
            if (AssetName(item).Contains(query, StringComparison.OrdinalIgnoreCase) ||
                DisplayName(item).Contains(query, StringComparison.OrdinalIgnoreCase))
                partial.Add(item);
        }

        return partial;
    }
}
