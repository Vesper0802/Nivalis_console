using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using Il2CppInterop.Runtime;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis;
using Nivalis.DevOptions;
using Nivalis.InventorySystem;
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
                print($"{cmd.Usage}  ?? {cmd.Help}");
                return;
            }

            // Names only: the full list no longer fits on screen one per line.
            print($"{All.Count} commands ??type 'help <name>' for detail:");
            var row = new StringBuilder("  ");
            foreach (var cmd in All)
            {
                if (row.Length + cmd.Name.Length > 70)
                {
                    print(row.ToString());
                    row.Clear().Append("  ");
                }

                row.Append(cmd.Name).Append("  ");
            }

            if (row.Length > 2)
                print(row.ToString());
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
                print($"'{query}' is ambiguous, {matches.Count} matches ??retry with a code:");
                for (var i = 0; i < Math.Min(matches.Count, 15); i++)
                    print($"  {GameRefs.ShortGuid(matches[i])}  {GameRefs.AssetName(matches[i])}");
                return;
            }

            var inventory = GameRefs.PlayerInventory;
            if (inventory == null)
            {
                print("No PlayerInventory in the scene ??load a save first.");
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
                print("No PlayerInventory in the scene ??load a save first.");
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
                print("No PlayerInventory in the scene ??load a save first.");
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
                print("No PlayerInventory in the scene ??load a save first.");
                return;
            }

            inventory.AddAllFurniture();
            print("Called AddAllFurniture().");
        });

        Add("shoplist", "shoplist [raw]", "Shows the shopping list with prices. 'raw' uses the game's own wording.",
            (args, print) =>
        {
            if (args.Length > 0 && args[0] == "raw")
            {
                var manager = GameRefs.ShoppingList;
                if (manager == null)
                {
                    print("No ShoppingListManager in the scene ??load a save first.");
                    return;
                }

                manager.CreateMessage(out var message);
                print(string.IsNullOrWhiteSpace(message)
                    ? "The game reports nothing on the shopping list."
                    : StripRichText(message));
                return;
            }

            var entries = ShoppingListEntries(print);
            if (entries == null)
                return;

            if (entries.Count == 0)
            {
                print("The shopping list is empty.");
                return;
            }

            var total = 0;
            foreach (var (item, missing, price) in entries)
            {
                total += missing * price;
                print($"  {missing.ToString().PadLeft(3)}x {GameRefs.DisplayName(item)}  ({GameRefs.AssetName(item)})  {missing * price} cents");
            }

            print($"{entries.Count} items short, {total} cents total. Use 'buylist' to get them.");
        });

        Add("buylist", "buylist [free]", "Puts everything the shopping list is short of into your inventory.",
            (args, print) =>
            {
                var entries = ShoppingListEntries(print);
                if (entries == null)
                    return;

                if (entries.Count == 0)
                {
                    print("The shopping list is empty ??nothing to buy.");
                    return;
                }

                var inventory = GameRefs.PlayerInventory;
                var free = args.Length > 0 && args[0] == "free";
                var total = 0;

                foreach (var (item, missing, price) in entries)
                {
                    inventory.AddItem(item, missing);
                    total += missing * price;
                    print($"  +{missing}x {GameRefs.DisplayName(item)}");
                }

                if (free)
                {
                    print($"Bought {entries.Count} items for nothing. Normally {total} cents.");
                    return;
                }

                // Paying keeps the economy honest; 'free' is there when you do not care.
                inventory.TakeMoney(total);
                print($"Bought {entries.Count} items for {total} cents. Money left: {inventory.Money}.");
            });

        // Taking an item away also clears the Articy variable a quest reads, so the plain form
        // keeps the items the story is watching and you have to ask for the rest.
        Add("clearitems", "clearitems [all]",
            "Empties the player inventory, keeping story items unless you say 'all'.",
            (args, print) =>
                StoryItems.Clear(args.Length > 0 &&
                                 string.Equals(args[0], "all", StringComparison.OrdinalIgnoreCase),
                                 print));

        Add("storyitems", "storyitems [all]",
            "Lists the items the story is watching, and which you are carrying.",
            (args, print) => StoryItems.Run(
                args.Length > 0 && string.Equals(args[0], "all", StringComparison.OrdinalIgnoreCase),
                print));

        Add("devmenu", "devmenu", "Opens the game's built-in developer option menu.", (args, print) =>
        {
            var menu = DevOptionMenuRevised.Instance;
            if (menu == null)
            {
                print("DevOptionMenuRevised.Instance is null ??the UI is not up yet.");
                return;
            }

            menu.Open();
            print("Opened the dev option menu.");
        });

        Add("dev", "dev [list|filter] | dev <index> [args]", "Lists or runs the game's own dev options.", (args, print) =>
        {
            if (!DevOptions.Ready)
            {
                print("Dev options have not been discovered yet ??load a save first.");
                return;
            }

            if (args.Length == 0 || args[0] == "list" || !int.TryParse(args[0], out var index))
            {
                var filter = args.Length > 0 && args[0] != "list" ? string.Join(" ", args) : null;
                var shown = 0;
                for (var i = 0; i < DevOptions.Count; i++)
                {
                    var name = DevOptions.Name(i);
                    if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    print($"  {i.ToString().PadLeft(3)}  {name} {DevOptions.Signature(i)}");
                    shown++;
                }

                print(filter == null
                    ? $"{shown} dev options. Run one with: dev <index> [args]"
                    : $"{shown} of {DevOptions.Count} dev options match '{filter}'.");
                return;
            }

            if (index < 0 || index >= DevOptions.Count)
            {
                print($"Index must be 0..{DevOptions.Count - 1}. Use 'dev list'.");
                return;
            }

            DevOptions.Invoke(index, args[1..], print);
        });

        Add("nospoil", "nospoil [on|off]", "Freezes food decay. The game has no built-in option for this.",
            (args, print) =>
            {
                if (args.Length > 0)
                    DecayFreeze.Enabled = args[0] is "1" or "on" or "true";
                else
                    DecayFreeze.Enabled = !DecayFreeze.Enabled;

                if (!DecayFreeze.Patched)
                {
                    print("Decay patch is not active ??check the BepInEx log for the reason.");
                    return;
                }

                print(DecayFreeze.Enabled
                    ? "Food decay frozen. Already-spoiled items stay spoiled."
                    : "Food decay back to normal.");
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
            sb.AppendLine($"Nivalis Nights item types - {items.Length} entries, then every property");
            sb.AppendLine("Paste a COMMAND cell into the console and press Enter; change the trailing number for a different amount.");
            sb.AppendLine("For details run iteminfo with the same code, e.g. iteminfo 04f867e4");
            sb.AppendLine("Rows flagged NOT-STORABLE cannot be placed in the player inventory.");
            sb.AppendLine("PRICE is in cents throughout, so 10000 is 100.");
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
                    // Which of a venue's two containers an ingredient lands in, and so which kind
                    // of storage furniture a menu actually calls for.
                    if (item.RequiresRefridgeration) flags.Add("chilled");
                    if (item.basicStorage > 0) flags.Add($"storage+{item.basicStorage}");
                    if (item.refridgeratedStorage > 0) flags.Add($"cold+{item.refridgeratedStorage}");
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

            // Venues go in the same file and the same columns as the items, because looking up a
            // venue and looking up an ingredient are the same job and splitting them across two
            // files means searching twice.
            var properties = Properties.DumpRows();
            sb.AppendLine();
            sb.AppendLine($"Properties - {properties.Count} entries. Paste a COMMAND cell the same way.");
            sb.AppendLine("'at=' is the district. 'permit=' is a specific item, not a level, and");
            sb.AppendLine("'have-permit' means you are carrying it. NOT-ACQUIREABLE ones are NPC-run.");
            sb.AppendLine();
            sb.AppendLine("COMMAND\tASSET NAME\tDISPLAY NAME\tPRICE\tFLAGS");
            foreach (var row in properties)
                sb.AppendLine(row);

            try
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
                print($"Wrote {items.Length} items and {properties.Count} properties to {path}");
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

        // Friendly names for the game's own dev options. Anything not covered here is still
        // reachable through 'dev list' and 'dev <index>'.
        AddDevAlias("recipes", "Discover All Recipes", "recipes",
            "Unlocks every meal recipe. Permanent.");
        AddDevAlias("recipe", "Discover Recipe", "recipe <name>",
            "Unlocks one recipe; a wrong name lists what is loaded.");
        AddDevAlias("fasttravel", "Unlock All Fast Travel", "fasttravel",
            "Unlocks every fast travel destination. Permanent.");
        AddDevAlias("unlockfish", "Unlock all fish", "unlockfish",
            "Marks every fish as discovered. Permanent.");
        AddDevAlias("unlockshops", "Unlock vendors and properties", "unlockshops",
            "Unlocks vendors and buyable properties. Permanent.");
        AddDevAlias("unlockboat", "Unlock boat", "unlockboat", "Gives you the boat.");
        AddDevAlias("venuelevel", "Set Venue Level", "venuelevel <1-5>",
            "Sets the level of the venue you are standing in.");
        AddDevAlias("allfurniture", "Add All Furniture Items", "allfurniture",
            "Adds one of every furniture item to your inventory.");
        AddDevAlias("farmfast", "Speed up farming", "farmfast", "Advances crop growth.");
        AddDevAlias("addvenue", "Add venue to player", "addvenue <name>",
            "Hands you one venue; a wrong name lists them all.");

        Add("dumpmenus", "dumpmenus", "Writes every venue's candidate recipes, ingredients and scores to a file.",
            (args, print) => MenuExport.Run(print));

        Add("restock", "restock [name fragment]", "Stocks each owned venue's fridge and cupboard with what its menu needs.",
            (args, print) => VenueRestock.Run(args.Length > 0 ? string.Join(" ", args) : null, print));

        Add("skill", "skill [name] [level]", "Shows your skill levels, or raises one to a level.",
            (args, print) => Skills.Run(args, print));

        // The game shows one property at a time at its own console, so there is nowhere to
        // compare prices or see which permit each one wants.
        Add("properties", "properties [filter]",
            "Lists every property with its price, rent and required permit.",
            (args, print) => Properties.List(args.Length > 0 ? string.Join(" ", args) : null, print));

        Add("acquire", "acquire <name> [rent]",
            "Takes or rents any property for free, including apartments and greenhouses.",
            (args, print) => Properties.Acquire(args, print));

        // Spoiled stock is worth nothing but still takes up storage, and the game leaves it in
        // place as RottenFood, which no screen clears in bulk.
        Add("spoiled", "spoiled [clear|sell] [venue]",
            "Reports food that has gone off in your bag and your venues, or bins it.",
            (args, print) => Spoiled.Run(args, print));

        // A venue's fridge and cupboard capacities are the sum of its placed furniture, and the
        // per-item contribution is authored where no screen shows it.
        Add("storage", "storage [filter]", "Lists furniture by how much venue storage it adds.",
            (args, print) => Storage.Run(args, print));

        Add("refuel", "refuel", "Fills the boat's tank, wherever the boat is.",
            (args, print) => BoatFuel.Run(print));

        // The dev option only reaches the venue you are standing in, and walking to each one to
        // level it is the slow part.
        Add("setlevel", "setlevel <name fragment|all> <level>",
            "Sets any venue's level from anywhere. 'all' does every venue you own.",
            (args, print) =>
        {
            if (args.Length < 2 || !int.TryParse(args[^1], out var level))
            {
                print("Usage: setlevel <name fragment|all> <level>. Use 'venues' for the names.");
                return;
            }

            var manager = GameRefs.Find<VenueManager>();
            if (manager == null)
            {
                print("No VenueManager in the scene — load a save first.");
                return;
            }

            var fragment = string.Join(" ", args[..^1]);
            var targets = Targets(manager, fragment, print);
            if (targets == null)
                return;

            foreach (var venue in targets)
                SetVenueLevel(manager, venue, level, print);

            if (targets.Count > 1)
                print($"Set {targets.Count} venues to level {level}.");
        });

        Add("venues", "venues", "Lists every venue and who owns it.", (args, print) =>
        {
            var venues = Venues();
            if (venues.Count == 0)
            {
                print("No venues loaded ??load a save first.");
                return;
            }

            foreach (var venue in venues)
                print($"  {Ownership(venue).PadRight(8)} tier {Tier(venue)}  {venue.name}");

            print($"{venues.Count} venues. Use 'addvenue <name>' or 'allvenues'.");
        });

        Add("allvenues", "allvenues", "Hands you every venue you do not already own.", (args, print) =>
        {
            if (!DevOptions.Ready)
            {
                print("The game's dev options are not available yet ??load a save first.");
                return;
            }

            var venues = Venues();
            if (venues.Count == 0)
            {
                print("No venues loaded ??load a save first.");
                return;
            }

            var added = 0;
            foreach (var venue in venues)
            {
                if (Ownership(venue) != "none")
                    continue;

                // Going through the dev option by name reuses the parameter marshalling that
                // resolves a Venue from text, rather than calling ForceAddToPlayer ourselves.
                var index = DevOptions.FindByName("Add venue to player", 1);
                if (index < 0)
                {
                    print("The game no longer has an 'Add venue to player' option. Check 'dev list'.");
                    return;
                }

                print($"  {venue.name}");
                DevOptions.Invoke(index, new[] { venue.name }, _ => { });
                added++;
            }

            print(added == 0 ? "You already own every venue." : $"Added {added} venues.");
        });

        AddDevAlias("timescale", "Set Game Time Multiplier", "timescale <multiplier> <timeScale>",
            "Speeds up or slows down the clock. 1 1 is normal.");
        AddDevAlias("settime", "Set Time Of Day", "settime <0..1> <pause>",
            "Jumps to a fraction of the day; 0.5 is noon.");
        AddDevAlias("pausetime", "Pause time of day", "pausetime", "Freezes the clock.");
        AddDevAlias("unpausetime", "Unpause time of day", "unpausetime", "Resumes the clock.");
        AddDevAlias("weather", "Set Weather Preset", "weather <preset>",
            "Sets the weather; a wrong name lists the presets.");

        AddDevAlias("speed", "Set player speed", "speed <value>", "Sets player movement speed.");
        AddDevAlias("fov", "Change camera FOV", "fov <degrees>", "Changes the camera field of view.");
        AddDevAlias("noclip", "Toggle No Clip", "noclip", "Toggles walking through walls.");
        AddDevAlias("stat", "Set stat", "stat <name> <value>",
            "Sets a player stat; a wrong name lists them.");
        AddDevAlias("curfewon", "Turn ON Curfew", "curfewon", "Starts curfew.");
        AddDevAlias("curfewoff", "Turn OFF Curfew", "curfewoff", "Ends curfew.");

        AddDevAlias("getvar", "Get Global Variable", "getvar <name>",
            "Reads a story variable. The value goes to the game's own log.");
        AddDevAlias("setbool", "Set Bool Global Variable", "setbool <name> <true|false>",
            "Sets a story variable. Can break quest state ??use with care.");
        AddDevAlias("setint", "Set Int Global Variable", "setint <name> <value>",
            "Sets a story variable. Can break quest state ??use with care.");
    }

    /// <summary>
    /// Reads the shopping list.
    ///
    /// An earlier version of this took the process down with an access violation inside
    /// coreclr, which no try/catch can intercept, so every call into the game is announced to
    /// the log before it happens: if it crashes again the last line written names the culprit.
    /// The scan is also limited to the kinds of item a shopping list can hold, which keeps us
    /// from poking the game with hundreds of furniture and prop types it never expects here.
    /// </summary>
    private static List<(ItemType Item, int Missing, int Price)> ShoppingListEntries(Action<string> print)
    {
        var log = Plugin.Instance.Log;

        log.LogInfo("shoplist: resolving PlayerInventory");
        if (GameRefs.PlayerInventory == null)
        {
            print("No PlayerInventory in the scene ??load a save first.");
            return null;
        }

        log.LogInfo("shoplist: resolving ShoppingListManager");
        var manager = GameRefs.ShoppingList;
        if (manager == null)
        {
            print("No ShoppingListManager in the scene ??load a save first.");
            return null;
        }

        log.LogInfo("shoplist: reading the ShoppingList dictionary");
        var list = manager.ShoppingList;
        if (list == null)
        {
            print("The game has not built a shopping list yet.");
            return null;
        }

        try { log.LogInfo($"shoplist: dictionary holds {list.Count} entries"); }
        catch (Exception e) { log.LogWarning($"shoplist: Count failed ({e.Message}); continuing"); }

        var fallback = Math.Max(1, ShoppingListManager.demandAmount);
        var entries = new List<(ItemType, int, int)>();
        var scanned = 0;

        log.LogInfo("shoplist: scanning item types");
        foreach (var item in GameRefs.ItemTypes())
        {
            if (!IsShoppable(item))
                continue;

            if (++scanned % 100 == 0)
                log.LogInfo($"shoplist: scanned {scanned} candidates");

            bool onList;
            try { onList = manager.IsOnList(item); }
            catch (Exception e)
            {
                log.LogWarning($"shoplist: IsOnList threw on {GameRefs.AssetName(item)}: {e.Message}");
                continue;
            }

            if (!onList)
                continue;

            var missing = fallback;
            try
            {
                var data = list[item];
                if (data != null)
                    missing = data.MissingItems;
            }
            catch (Exception e)
            {
                log.LogWarning($"shoplist: no entry for {GameRefs.AssetName(item)} ({e.Message}); using {fallback}");
            }

            if (missing <= 0)
                continue;

            var price = 0;
            try { price = item.BaseMarketPrice; } catch { }
            entries.Add((item, missing, price));
        }

        log.LogInfo($"shoplist: done, {scanned} candidates scanned, {entries.Count} on the list");
        return entries;
    }

    /// <summary>Venues are ScriptableObjects, so every one in the build is reachable.</summary>
    /// <summary>
    /// The venues a level change should apply to. 'all' means the ones you own, because a level
    /// only means anything on a venue that is yours.
    /// </summary>
    private static List<Venue> Targets(VenueManager manager, string fragment, Action<string> print)
    {
        if (string.Equals(fragment, "all", StringComparison.OrdinalIgnoreCase))
        {
            var owned = new List<Venue>();
            try
            {
                var player = GameRefs.Find<PlayerManager>()?.LocalPlayer;
                var results = new Il2CppSystem.Collections.Generic.List<Venue>();
                player?.GetOwnedVenues(results);
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

            return owned.OrderBy(v => v.name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        var matches = Venues()
            .Where(v => v.name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1)
            return matches;

        print(matches.Count == 0
            ? $"No venue matches '{fragment}'. Use 'venues' for the list."
            : $"'{fragment}' matches {matches.Count} venues — be more specific, or use 'all':");
        foreach (var venue in matches.Take(20))
            print($"  {venue.name}");
        return null;
    }

    private static void SetVenueLevel(VenueManager manager, Venue venue, int level, Action<string> print)
    {
        VenueAreaGhost ghost;
        try { ghost = manager.GetRuntimeData(venue); }
        catch (Exception e)
        {
            print($"  {venue.name}: could not reach it ({e.Message}).");
            return;
        }

        if (ghost == null)
        {
            print($"  {venue.name}: no runtime data, so its level cannot be set.");
            return;
        }

        try
        {
            var before = ghost.CurrentLevel;
            // SetLevel rather than the CurrentLevel setter, so the game applies the level's own
            // limits and raises its level-changed event.
            ghost.SetLevel(level);
            print($"  {venue.name}: level {before} -> {ghost.CurrentLevel}, menu limit " +
                  $"{ghost.MenuLimit}, staff {ghost.StaffLimit}, tables {ghost.TableLimit}.");
        }
        catch (Exception e)
        {
            print($"  {venue.name}: could not set the level ({e.Message}).");
        }
    }

    private static List<Venue> Venues()
    {
        var found = new List<Venue>();
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Venue>()))
        {
            var venue = obj?.TryCast<Venue>();
            if (venue != null)
                found.Add(venue);
        }

        found.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    /// <summary>
    /// Venue.OwnershipType is the authored default and reads None even for venues you hold, so
    /// ownership comes from the player instead.
    /// </summary>
    private static string Ownership(Venue venue)
    {
        try
        {
            var player = GameRefs.Find<PlayerManager>()?.LocalPlayer;
            if (player == null)
                return "?";
            return player.IsOwningVenue(venue)
                ? player.GetPropertyOwnershipState(venue).ToString().ToLowerInvariant()
                : "none";
        }
        catch { return "?"; }
    }

    private static string Tier(Venue venue)
    {
        try { return venue.Tier.ToString(); }
        catch { return "?"; }
    }

    /// <summary>
    /// Drops TextMeshPro markup. Strings the game builds for its own UI carry things like
    /// &lt;sprite="Keyboard_Mouse_Combined" name="R"&gt;, which is noise in a plain text console.
    /// </summary>
    private static string StripRichText(string text) =>
        Regex.Replace(text ?? "", "<[^>]+>", "").Replace("  ", " ").Trim();

    /// <summary>Only consumables reach a shopping list, so nothing else is worth asking about.</summary>
    private static bool IsShoppable(ItemType item)
    {
        try
        {
            return item.IsIngredient || item.IsMeal || item.IsDrink || item.IsSeed || item.IsPlant;
        }
        catch
        {
            return false;
        }
    }

    private static void Add(string name, string usage, string help, CommandHandler run) =>
        All.Add(new Command { Name = name, Usage = usage, Help = help, Run = run });

    /// <summary>
    /// Wraps one of the game's own dev options in a memorable name so you do not have to
    /// remember its index. Resolution happens by label at call time, not by index at startup.
    /// </summary>
    private static void AddDevAlias(string name, string label, string usage, string help)
    {
        Add(name, usage, help, (args, print) =>
        {
            if (!DevOptions.Ready)
            {
                print("The game's dev options are not available yet ??load a save first.");
                return;
            }

            var index = DevOptions.FindByName(label, args.Length);
            if (index < 0)
            {
                print($"The game has no dev option called '{label}' any more. Check 'dev list'.");
                return;
            }

            DevOptions.Invoke(index, args, print);
        });
    }

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
