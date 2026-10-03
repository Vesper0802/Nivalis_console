using System;
using System.Collections.Generic;
using System.Linq;
using Nivalis.InventorySystem;

namespace NivalisDevUnlock;

/// <summary>
/// Which furniture adds how much venue storage. A venue's fridge and cupboard capacities are the
/// sum of what its placed furniture contributes, and the per-item figures are authored on the
/// ItemType, which no screen in the game puts side by side — so without this the only way to
/// compare two cupboards is to buy one and watch the total move.
/// </summary>
internal static class Storage
{
    private readonly struct Row
    {
        public readonly string Asset;
        public readonly string Display;
        public readonly int Cold;
        public readonly int Dry;
        public readonly int Price;
        public readonly string Code;

        public Row(string asset, string display, int cold, int dry, int price, string code)
        {
            Asset = asset; Display = display; Cold = cold; Dry = dry; Price = price; Code = code;
        }

        public int Total => Cold + Dry;
    }

    public static void Run(string[] args, Action<string> print)
    {
        var filter = args.Length > 0 ? string.Join(" ", args) : null;
        var rows = new List<Row>();

        foreach (var item in GameRefs.ItemTypes())
        {
            // Tables and washing basins carry these fields too, so the scan is over every item
            // type rather than over the furniture flag.
            var cold = Int(() => item.refridgeratedStorage);
            var dry = Int(() => item.basicStorage);
            if (cold + dry <= 0)
                continue;

            var asset = GameRefs.AssetName(item);
            var display = GameRefs.DisplayName(item);
            if (filter != null && !Hit(asset, filter) && !Hit(display, filter))
                continue;

            rows.Add(new Row(asset, display, cold, dry, Int(() => item.BasePrice),
                GameRefs.ShortGuid(item)));
        }

        if (rows.Count == 0)
        {
            print(filter == null
                ? "No item type reports any storage. Load a save first — item types arrive with the areas."
                : $"No storage furniture matches '{filter}'.");
            return;
        }

        print($"{rows.Count} item types add storage, biggest first. " +
              "A venue holds the sum of what is placed in it.");
        print("  CODE      COLD   DRY  PRICE    PER 10k  NAME");

        foreach (var row in rows.OrderByDescending(r => r.Total).ThenBy(r => r.Price))
        {
            // The game stocks 10 of each ingredient, so storage divided by ten is the number of
            // distinct ingredients a venue can carry, which is what caps a menu's size.
            var perCost = row.Price > 0 ? $"{row.Total * 10000f / row.Price,7:0}" : "      -";
            print($"  {row.Code}  {row.Cold,4}  {row.Dry,4}  {row.Price,6}  {perCost}  " +
                  $"{row.Asset}{(row.Display == row.Asset ? "" : $" [{row.Display}]")}");
        }

        var cheapest = rows.Where(r => r.Price > 0).OrderByDescending(r => r.Total * 1f / r.Price)
                           .FirstOrDefault();
        if (cheapest.Asset != null)
            print($"Best value is {cheapest.Asset} at {cheapest.Total} storage for " +
                  $"{cheapest.Price} ({cheapest.Total / 10} ingredients' worth).");
    }

    private static bool Hit(string value, string filter) =>
        value != null && value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static int Int(Func<int> read)
    {
        try { return read(); }
        catch { return 0; }
    }
}
