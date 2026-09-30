using System.IO;
using System.Text;
using BepInEx;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis.DevOptions;
using UnityEngine;

namespace NivalisDevUnlock;

internal delegate void CommandHandler(string[] args, Action<string> print);

internal sealed class Command
{
    public string Name;
    public string Usage;
    public string Help;
    public CommandHandler Run;
}

internal static class Commands
{
    public static readonly List<Command> All = new();

    static Commands()
    {
        Add("help", "help [command]", "Lists commands, or explains one.", (args, print) =>
        {
            if (args.Length > 0)
            {
                var cmd = Find(args[0]);
                if (cmd == null)
                {
                    print($"No such command '{args[0]}'.");
                    return;
                }
                print($"{cmd.Usage}  —  {cmd.Help}");
                return;
            }

            print("Commands (type 'help <name>' for detail):");
            foreach (var cmd in All)
                print($"  {cmd.Name.PadRight(12)} {cmd.Help}");
        });

        Add("items", "items [filter]", "Lists loaded item types, optionally filtered.", (args, print) =>
        {
            var filter = args.Length > 0 ? string.Join(" ", args) : null;
            var items = GameRefs.ItemTypes();
            var shown = 0;

            foreach (var item in items)
            {
                var asset = GameRefs.AssetName(item);
                var display = GameRefs.DisplayName(item);
                if (filter != null &&
                    !asset.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !display.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (shown++ >= 60)
                {
                    print("  ... more matches, narrow the filter.");
                    break;
                }

                var label = string.IsNullOrEmpty(display) || display == asset ? asset : $"{asset}  ({display})";
                print($"  {GameRefs.ShortGuid(item)}  {label}");
            }

            if (shown == 0)
                print(filter == null ? "No item types loaded yet." : $"Nothing matching '{filter}'.");
            else
                print($"{items.Length} item types loaded.");
        });

        Add("iteminfo", "iteminfo <item|code>", "Shows an item's codes and properties.", (args, print) =>
        {
            if (args.Length == 0)
            {
                print("Usage: iteminfo <item|code>");
                return;
            }

            var query = string.Join(" ", args);
            var matches = GameRefs.Resolve(query);

            if (matches.Count == 0)
            {
                print($"Nothing matching '{query}'.");
                return;
            }

            if (matches.Count > 1)
            {
                print($"'{query}' matches {matches.Count} items:");
                foreach (var m in matches.Take(15))
                    print($"  {GameRefs.ShortGuid(m)}  {GameRefs.AssetName(m)}");
                return;
            }

            var item = matches[0];
            print($"Asset name : {GameRefs.AssetName(item)}");
            print($"Display    : {GameRefs.DisplayName(item)}");
            print($"Guid       : {GameRefs.Guid(item)}");
            print($"ArticyGuid : {GameRefs.ArticyGuid(item)}");

            try
            {
                print($"BasePrice  : {item.BasePrice}   MarketPrice: {item.BaseMarketPrice}");
                print($"Storable   : {item.IsPlayerStorable}   Furniture: {item.IsFurniture}   " +
                      $"Ingredient: {item.IsIngredient}");
                print($"Useable    : {item.IsUseable}   Equippable: {item.IsEquippable}   " +
                      $"Fish: {item.IsFish}   Plant: {item.IsPlant}");
            }
            catch (Exception e)
            {
                print($"(some properties unavailable: {e.Message})");
            }
        });

        Add("give", "give <item|code> [amount]", "Adds an item to the player inventory.", (args, print) =>
        {
            if (args.Length == 0)
            {
                print("Usage: give <item> [amount]");
                return;
            }

            var amount = 1;
            var nameParts = args;
            if (args.Length > 1 && int.TryParse(args[^1], out var parsed))
            {
                amount = parsed;
                nameParts = args[..^1];
            }

            if (amount < 1)
            {
                print("Amount must be at least 1.");
                return;
            }

            var query = string.Join(" ", nameParts);
            var matches = GameRefs.Resolve(query);

            if (matches.Count == 0)
            {
                print($"No item matching '{query}'. Try 'items {query}'.");
                return;
            }

            if (matches.Count > 1)
            {
                print($"'{query}' is ambiguous, {matches.Count} matches — retry with a code:");
                for (var i = 0; i < Math.Min(matches.Count, 15); i++)
                    print($"  {GameRefs.ShortGuid(matches[i])}  {GameRefs.AssetName(matches[i])}");
                return;
            }

            var inventory = GameRefs.PlayerInventory;
            if (inventory == null)
            {
                print("No PlayerInventory in the scene — load a save first.");
                return;
            }

            var item = matches[0];
            inventory.AddItem(item, amount);
            print($"Gave {amount}x {GameRefs.AssetName(item)}.");
        });

        Add("money", "money [amount]", "Shows money, or sets it. Value is in Lim cents.", (args, print) =>
        {
            var inventory = GameRefs.PlayerInventory;
            if (inventory == null)
            {
                print("No PlayerInventory in the scene — load a save first.");
                return;
            }

            if (args.Length == 0)
            {
                print($"Money: {inventory.Money} cents.");
                return;
            }

            if (!int.TryParse(args[0], out var value))
            {
                print($"'{args[0]}' is not a number.");
                return;
            }

            var before = inventory.Money;
            inventory.Money = value;
            print($"Money: {before} -> {inventory.Money} cents.");
        });

        Add("addmoney", "addmoney <amount>", "Adds (or subtracts) money in Lim cents.", (args, print) =>
        {
            var inventory = GameRefs.PlayerInventory;
            if (inventory == null)
            {
                print("No PlayerInventory in the scene — load a save first.");
                return;
            }

            if (args.Length == 0 || !int.TryParse(args[0], out var delta))
            {
                print("Usage: addmoney <amount>");
                return;
            }

            var before = inventory.Money;
            if (delta >= 0)
                inventory.ReceiveMoney(delta);
            else
                inventory.TakeMoney(-delta);
            print($"Money: {before} -> {inventory.Money} cents.");
        });

        Add("furniture", "furniture", "Grants every furniture item (the game's own helper).", (args, print) =>
        {
            var inventory = GameRefs.PlayerInventory;
            if (inventory == null)
            {
                print("No PlayerInventory in the scene — load a save first.");
                return;
            }

            inventory.AddAllFurniture();
            print("Called AddAllFurniture().");
        });

        Add("clearitems", "clearitems", "Empties the player inventory.", (args, print) =>
        {
            var inventory = GameRefs.PlayerInventory;
            if (inventory == null)
            {
                print("No PlayerInventory in the scene — load a save first.");
                return;
            }

            inventory.ClearItems();
            print("Inventory cleared.");
        });

        Add("devmenu", "devmenu", "Opens the game's built-in developer option menu.", (args, print) =>
        {
            var menu = DevOptionMenuRevised.Instance;
            if (menu == null)
            {
                print("DevOptionMenuRevised.Instance is null — the UI is not up yet.");
                return;
            }

            menu.Open();
            print("Opened the dev option menu.");
        });

        Add("dev", "dev list | dev <index>", "Lists or invokes the game's own dev options.", (args, print) =>
        {
            var attributes = DevOptionMenuRevised._methodDisplayString;
            if (attributes == null)
            {
                print("Dev options have not been discovered yet.");
                return;
            }

            if (args.Length == 0 || args[0] == "list")
            {
                print($"{attributes.Length} dev options:");
                for (var i = 0; i < attributes.Length; i++)
                {
                    var attr = attributes[i];
                    if (attr == null)
                        continue;
                    print($"  {i.ToString().PadLeft(3)}  {attr.Name}");
                }
                return;
            }

            if (!int.TryParse(args[0], out var index) || index < 0 || index >= attributes.Length)
            {
                print($"Index must be 0..{attributes.Length - 1}. Use 'dev list'.");
                return;
            }

            var menu = DevOptionMenuRevised.Instance;
            if (menu == null)
            {
                print("DevOptionMenuRevised.Instance is null — the UI is not up yet.");
                return;
            }

            // Only zero-argument options work this way; anything needing parameters has to
            // go through the real menu UI, which is what the failure message points at.
            try
            {
                menu.InvokeMethod(index, new Il2CppReferenceArray<Il2CppSystem.Object>(0));
                print($"Invoked dev option {index} ({attributes[index].Name}).");
            }
            catch (Exception e)
            {
                print($"Invoke failed: {e.Message}");
                print("It probably takes parameters — use 'devmenu' and run it from the UI.");
            }
        });

        Add("refresh", "refresh", "Rebuilds the item type cache.", (args, print) =>
        {
            GameRefs.InvalidateItemCache();
            print($"{GameRefs.ItemTypes(true).Length} item types loaded.");
        });

        Add("dumpitems", "dumpitems [filename]", "Writes every item type to a text file.", (args, print) =>
        {
            var name = args.Length > 0 ? args[0] : "nivalis-items.txt";
            var path = Path.Combine(Paths.BepInExRootPath, name);
            var items = GameRefs.ItemTypes();

            var sb = new StringBuilder();
            sb.AppendLine($"Nivalis Nights item types — {items.Length} entries");
            sb.AppendLine("Paste a COMMAND cell into the console and press Enter; change the trailing number for a different amount.");
            sb.AppendLine("For details run iteminfo with the same code, e.g. iteminfo 04f867e4");
            sb.AppendLine("Rows flagged NOT-STORABLE cannot be placed in the player inventory.");
            sb.AppendLine();
            sb.AppendLine("COMMAND\tASSET NAME\tDISPLAY NAME\tPRICE\tFLAGS");

            foreach (var item in items)
            {
                var flags = new List<string>();
                try
                {
                    if (item.IsFurniture) flags.Add("furniture");
                    if (item.IsIngredient) flags.Add("ingredient");
                    if (item.IsMeal) flags.Add("meal");
                    if (item.IsDrink) flags.Add("drink");
                    if (item.IsFish) flags.Add("fish");
                    if (item.IsPlant) flags.Add("plant");
                    if (item.IsSeed) flags.Add("seed");
                    if (item.IsUseable) flags.Add("useable");
                    if (item.IsEquippable) flags.Add("equippable");
                    if (!item.IsPlayerStorable) flags.Add("NOT-STORABLE");
                }
                catch
                {
                    flags.Add("?");
                }

                var price = "?";
                try { price = item.BasePrice.ToString(); } catch { }

                sb.Append("give ").Append(GameRefs.ShortGuid(item)).Append(" 1").Append('\t')
                  .Append(GameRefs.AssetName(item)).Append('\t')
                  .Append(GameRefs.DisplayName(item)).Append('\t')
                  .Append(price).Append('\t')
                  .Append(string.Join(",", flags))
                  .AppendLine();
            }

            try
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                print($"Wrote {items.Length} items to {path}");
            }
            catch (Exception e)
            {
                print($"Could not write the file: {e.Message}");
            }
        });

        Add("copylog", "copylog", "Copies the whole console output to the clipboard.", (args, print) =>
        {
            var text = ConsoleWindow.OutputText;
            GUIUtility.systemCopyBuffer = text;
            print($"Copied {text.Length} characters to the clipboard.");
        });

        Add("uiscale", "uiscale [factor]", "Shows or sets the console magnification.", (args, print) =>
        {
            if (args.Length == 0)
            {
                print($"UiScale is {Plugin.UiScale.Value}.");
                return;
            }

            if (!float.TryParse(args[0], out var scale))
            {
                print($"'{args[0]}' is not a number.");
                return;
            }

            Plugin.UiScale.Value = Mathf.Clamp(scale, 0.5f, 6f);
            print($"UiScale is now {Plugin.UiScale.Value}. Saved to the config file.");
        });
    }

    private static void Add(string name, string usage, string help, CommandHandler run) =>
        All.Add(new Command { Name = name, Usage = usage, Help = help, Run = run });

    public static Command Find(string name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static void Execute(string line, Action<string> print)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        var cmd = Find(parts[0]);
        if (cmd == null)
        {
            print($"Unknown command '{parts[0]}'. Type 'help'.");
            return;
        }

        try
        {
            cmd.Run(parts[1..], print);
        }
        catch (Exception e)
        {
            print($"Error: {e.Message}");
        }
    }
}
