using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;
using UnityEngine;

namespace NivalisDevUnlock;

/// <summary>
/// What every property costs, which permit it asks for, and whether it is yours. The game only
/// shows one property at a time, at its own console, so there is nowhere to compare them — and
/// the permit a property wants is a specific item rather than a level, which is the part that
/// decides whether you can buy it at all.
/// </summary>
internal static class Properties
{
    public static void List(string filter, Action<string> print)
    {
        var all = All();
        if (all.Count == 0)
        {
            print("No properties loaded — load a save first.");
            return;
        }

        var shown = string.IsNullOrWhiteSpace(filter)
            ? all
            : all.Where(p => p.name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (shown.Count == 0)
        {
            print($"No property matches '{filter}'. Run 'properties' for the whole list.");
            return;
        }

        print("  OWNED  BUY        RENT      PERMIT            KIND        NAME");
        foreach (var property in shown.OrderBy(Kind).ThenBy(p => Buy(p)))
        {
            var permit = Permit(property, out var have);
            print($"  {Owned(property),-6} {Money(Buy(property)),-10} {Money(Rent(property)),-9} " +
                  $"{permit + (have ? " *" : ""),-17} {Kind(property),-11} {property.name}" +
                  $"{(Acquireable(property) ? "" : "  (not acquireable)")}");
        }

        print($"{shown.Count} of {all.Count} properties. A '*' means you hold that permit. " +
              "Use 'acquire <name>' to take one, or 'acquire <name> rent' to rent it.");
    }

    /// <summary>
    /// Takes ownership without paying or spending a permit. The game's own route is its property
    /// console, which charges the price and consumes the permit; this is the shortcut, and it
    /// covers apartments and greenhouses, which the venue dev option cannot reach.
    /// </summary>
    public static void Acquire(string[] args, Action<string> print)
    {
        if (args.Length == 0)
        {
            print("Usage: acquire <name fragment> [rent]. Run 'properties' for the names.");
            return;
        }

        var rent = string.Equals(args[^1], "rent", StringComparison.OrdinalIgnoreCase);
        var fragment = string.Join(" ", rent ? args[..^1] : args);
        if (string.IsNullOrWhiteSpace(fragment))
        {
            print("Usage: acquire <name fragment> [rent].");
            return;
        }

        var matches = All()
            .Where(p => p.name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count != 1)
        {
            print(matches.Count == 0
                ? $"No property matches '{fragment}'. Run 'properties' for the names."
                : $"'{fragment}' matches {matches.Count} properties — be more specific:");
            foreach (var property in matches.Take(12))
                print($"  {property.name}");
            return;
        }

        var target = matches[0];
        var manager = GameRefs.Find<PropertyManager>();
        if (manager == null)
        {
            print("No PropertyManager in the scene — load a save first.");
            return;
        }

        if (!Acquireable(target))
            print($"{target.name} is not marked acquireable, so this may not hold.");

        if (rent)
        {
            // Renting has its own entry point, which raises the property's rent-started event.
            try
            {
                manager.StartRenting(target);
                print($"{target.name}: now rented ({Owned(target)}), rent {Money(Rent(target))}.");
            }
            catch (Exception e) { print($"Could not rent {target.name}: {e.Message}"); }
            return;
        }

        // Interop models interfaces as classes, so the player is cast to the owner interface
        // rather than passed straight in.
        var player = GameRefs.Find<PlayerManager>()?.LocalPlayer;
        var owner = player?.TryCast<IPropertyOwner>();
        if (owner == null)
        {
            print("Could not reach the player as a property owner. For venues, 'addvenue' goes " +
                  "through the game's dev option instead.");
            return;
        }

        try
        {
            manager.AddOwnedProperty(owner, target, OwnershipType.Purchase);
            print($"{target.name}: now {Owned(target)}, and it cost nothing.");
        }
        catch (Exception e) { print($"Could not take {target.name}: {e.Message}"); }
    }

    private static List<BaseProperty> All()
    {
        var found = new List<BaseProperty>();
        try
        {
            // Every venue, apartment and greenhouse derives from BaseProperty, so one sweep
            // covers all three kinds.
            foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<BaseProperty>()))
            {
                var property = obj?.TryCast<BaseProperty>();
                if (property != null)
                    found.Add(property);
            }
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"properties: sweep failed: {e.Message}");
        }

        return found;
    }

    private static string Kind(BaseProperty property)
    {
        try
        {
            if (property.TryCast<Venue>() != null) return "venue";
            if (property.TryCast<Nivalis.Apartment.Apartment>() != null) return "apartment";
            if (property.TryCast<Greenhouse>() != null) return "greenhouse";
        }
        catch { }

        return "?";
    }

    /// <summary>
    /// PlayerOwned is the computed answer; the authored OwnershipType on the asset reads None
    /// even for a property you hold, so it is not consulted.
    /// </summary>
    private static string Owned(BaseProperty property)
    {
        try { return property.PlayerOwned ? "yours" : "-"; }
        catch { return "?"; }
    }

    /// <summary>
    /// The permit is a specific item, not a level, so holding permit 4 does not satisfy a
    /// property that asks for permit 2.
    /// </summary>
    private static string Permit(BaseProperty property, out bool have)
    {
        have = false;
        try
        {
            var permit = property.RequiredPermit;
            if (permit == null)
                return "none";

            var name = GameRefs.AssetName(permit);
            try
            {
                var items = GameRefs.PlayerInventory?.Items;
                have = items != null && items.GetItemCount(permit) > 0;
            }
            catch { }

            return name;
        }
        catch { return "?"; }
    }

    private static bool Acquireable(BaseProperty property)
    {
        try { return property.IsAcquireable; }
        catch { return true; }
    }

    /// <summary>
    /// The manager's price rather than the asset's BuyCost field. Every acquireable venue
    /// authors the same BuyCost while authoring quite different rents, which reads like a
    /// placeholder, and GetBuyPrice is what the game asks when it charges you.
    /// </summary>
    private static int Buy(BaseProperty property)
    {
        try
        {
            var manager = GameRefs.Find<PropertyManager>();
            if (manager != null)
                return manager.GetBuyPrice(property);
        }
        catch { }

        try { return property.BuyCost; }
        catch { return -1; }
    }

    private static int Rent(BaseProperty property)
    {
        try { return property.RentCost; }
        catch { return -1; }
    }

    /// <summary>Money is held in cents, which reads as a wall of digits at these prices.</summary>
    private static string Money(int cents) => cents < 0 ? "?" : (cents / 100).ToString("N0");
}
