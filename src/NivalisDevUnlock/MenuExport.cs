using System.Text;
using BepInEx;
using Il2CppInterop.Runtime;
using Nivalis;
using Nivalis.CraftingSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;
using UnityEngine;

namespace NivalisDevUnlock;

/// <summary>
/// Exports everything needed to plan venue menus: which recipes each owned venue may serve, how
/// many distinct ingredients each costs, and how the venue's local demographics score it.
/// The game's own judgement maths is called rather than reimplemented, so the scores match what
/// customers actually use.
/// </summary>
internal static class MenuExport
{
    public static void Run(Action<string> print)
    {
        var log = Plugin.Instance.Log;

        var venues = All<Venue>();
        if (venues.Count == 0)
        {
            print("No venues loaded — load a save first.");
            return;
        }

        var recipes = All<MealRecipeDefinition>();
        var demographics = All<LocationDemographics>();

        var player = GameRefs.Find<PlayerManager>()?.LocalPlayer;
        if (player == null)
            print("Note: no local player found, so ownership is unknown; every venue is treated as owned.");

        var owned = OwnedVenues(player, venues);
        var venueManager = GameRefs.Find<VenueManager>();
        if (venueManager == null)
            print("Note: no VenueManager found, so menu limits and storage capacity are unavailable.");

        // Both of these are what turn raw numbers into a customer-facing score. If interop never
        // bound them the export still carries exclusivity and calories, so planning can be done
        // by hand instead of the file silently filling with zeroes.
        var canPrecalc = Interop.HasNativeMethod(typeof(DemographicGroup), "PrecalculateRecipeJudgementInfo");
        var canJudge = Interop.HasNativeMethod(typeof(DemographicGroup), "CalculateRecipeJudgementBreakdown");
        if (!canPrecalc || !canJudge)
            print("Note: the judgement maths is unavailable; score columns will read 'n/a'.");

        var sb = new StringBuilder();
        sb.AppendLine($"# Nivalis Nights menu planning export — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# {venues.Count} venues ({owned.Count} owned), {recipes.Count} recipes, " +
                      $"{demographics.Count} location demographics");
        sb.AppendLine("# A venue may serve any unlocked recipe whose type is in its allowed types;");
        sb.AppendLine("# an empty type list restricts nothing, and the per-venue 'recipes' array");
        sb.AppendLine("# is only authored for fixed-menu chains.");
        sb.AppendLine("# score = representation-weighted FinalScore over the location's demographics.");
        sb.AppendLine();

        sb.AppendLine("## ALL VENUES");
        foreach (var venue in venues)
            sb.AppendLine($"VENUE\t{venue.name}\tloc={LocationName(venue)}\ttier={S(() => venue.Tier.ToString())}" +
                          $"\towned={(owned.Contains(venue) ? "yes" : "no")}" +
                          $"\town={OwnershipState(player, venue)}" +
                          $"\ttypes={AllowedTypes(venue)}\tauthored={S(() => venue.Recipes?.Length.ToString()) ?? "0"}");
        sb.AppendLine();

        sb.AppendLine("## RECIPES");
        sb.AppendLine("RECIPE\tTYPE\tPRICE\tINGPRICE\tEXCLMULT\tDRINK\tUNLOCKED\tDISABLED\tPLAYERONLY" +
                      "\tNSLOTS\tNCUSTOM\tPROCESSORS\tINGREDIENTS");
        foreach (var recipe in recipes)
            sb.AppendLine($"{recipe.name}\t{S(() => recipe.RecipeType.ToString())}" +
                          $"\t{F(N(() => recipe.CurrentBasePrice))}\t{F(N(() => recipe.IngredientsPrice))}" +
                          $"\t{F(N(() => recipe.ExclusivityMultiplier))}\t{S(() => recipe.Drink.ToString())}" +
                          // Interop models il2cpp interfaces as classes, so this needs a cast at runtime.
                          $"\t{S(() => recipe.TryCast<IUnlockableRecipe>()?.IsUnlocked.ToString())}" +
                          $"\t{S(() => recipe.Disabled.ToString())}\t{S(() => recipe.PlayerOnly.ToString())}" +
                          $"\t{SlotCount(recipe)}\t{CustomSlotCount(recipe)}" +
                          $"\t{S(() => recipe.GetNumberOfRequiredProcessors().ToString())}" +
                          $"\t{IngredientSummary(recipe)}");
        sb.AppendLine();

        sb.AppendLine("## RECIPE SLOTS");
        sb.AppendLine("# cust=true slots accept any ingredient carrying one of the listed tags, so those");
        sb.AppendLine("# are the slots where dishes can be made to share a shopping list.");
        foreach (var recipe in recipes)
        {
            var inputs = S2(() => recipe.Inputs);
            if (inputs == null)
                continue;
            sb.AppendLine($"RECIPE\t{recipe.name}");
            foreach (var input in inputs)
            {
                if (input == null)
                    continue;
                sb.AppendLine($"  IN\t{S(() => input.DefaultItem?.name)}\tx{S(() => input.Amount.ToString())}" +
                              $"\tslot={S(() => input.Slot.ToString())}\tproc={S(() => input.ProcessingType.ToString())}" +
                              $"\tcust={S(() => input.IsCustomizable.ToString())}\ttags={TagNames(input)}");
            }
        }
        sb.AppendLine();

        // Every location scored, whether or not you have a venue there. The owned-venue section
        // below can only speak for the venues you hold, which is no use when the question is
        // which venue to buy, and no use to anyone else reading the file.
        sb.AppendLine("## LOCATIONS");
        sb.AppendLine("# Scores here come from the location's own demographic mix. A venue with an");
        sb.AppendLine("# authored DemographicPriority weights every group equally instead, so check");
        sb.AppendLine("# DEMOSOURCE in the owned section before trusting these for such a venue.");
        var locationRows = 0;
        foreach (var demo in demographics.OrderBy(d => S(() => d.Location?.name) ?? "?"))
        {
            var groups = LocationGroups(demo);
            var prefs = LocalPreferences(demo, out var prefNames);

            sb.AppendLine();
            sb.AppendLine($"LOCATION\t{S(() => demo.Location?.name) ?? "?"}" +
                          $"\tdisplay={S(() => demo.Location?.DisplayName) ?? "?"}" +
                          $"\tpopulation={S(() => demo.PopulationCount.ToString())}" +
                          $"\tgroups={groups.Count}");
            foreach (var (group, weight) in groups)
                sb.AppendLine($"  DEMO\t{group.name}\trep={F(weight)}\texclPref={F(N(() => group.ExclusivityPreference))}" +
                              $"\texclImp={F(N(() => group.ExclusivityImportance))}" +
                              $"\tvalImp={F(N(() => group.ValueImportance))}" +
                              $"\tpriceImp={F(N(() => group.PriceImportance))}");
            sb.AppendLine($"  LOCALPREF\t{prefNames}");
            sb.AppendLine($"  LOCAVGEXCL\t{F(N(() => demo.GetAverageExclusivityPreference()))}");

            sb.AppendLine("  SCORE\tRECIPE\tTYPE\tPRICE\tCALORIES\tEXCLUSIVITY\tLOCVALUE\tSCORE\tINGREDIENTS");
            foreach (var recipe in recipes)
            {
                var judged = Judge(recipe, prefs, groups, canPrecalc, canJudge);
                sb.AppendLine($"  SCORE\t{recipe.name}\t{S(() => recipe.RecipeType.ToString())}\t{F(judged.Price)}" +
                              $"\t{F(judged.Calories)}\t{F(judged.Exclusivity)}\t{F(judged.LocationValue)}" +
                              $"\t{judged.Score}\t{IngredientSummary(recipe)}");
                locationRows++;
            }
        }
        sb.AppendLine();

        sb.AppendLine("## OWNED VENUES AND THEIR CANDIDATES");
        var rows = 0;
        foreach (var venue in owned)
        {
            var locationName = LocationName(venue);
            var demo = demographics.FirstOrDefault(d => S(() => d.Location?.name) == locationName);
            var allowed = AllowedTypeSet(venue);

            sb.AppendLine();
            sb.AppendLine($"OWNED\t{venue.name}\tloc={locationName}\ttier={S(() => venue.Tier.ToString())}" +
                          $"\ttypes={AllowedTypes(venue)}");

            var groups = WeightedGroups(venue, demo, out var source);
            sb.AppendLine($"  DEMOSOURCE\t{source}\tcount={groups.Count}");
            foreach (var (group, weight) in groups)
                sb.AppendLine($"  DEMO\t{group.name}\trep={F(weight)}\texclPref={F(N(() => group.ExclusivityPreference))}" +
                              $"\texclImp={F(N(() => group.ExclusivityImportance))}" +
                              $"\tvalImp={F(N(() => group.ValueImportance))}" +
                              $"\tpriceImp={F(N(() => group.PriceImportance))}");

            var prefs = LocalPreferences(demo, out var prefNames);
            sb.AppendLine($"  LOCALPREF\t{prefNames}");
            if (demo != null)
                sb.AppendLine($"  LOCAVGEXCL\t{F(N(() => demo.GetAverageExclusivityPreference()))}" +
                              $"\tpopulation={S(() => demo.PopulationCount.ToString())}");
            sb.AppendLine($"  CURRENTMENU\t{CurrentMenu(venue)}");
            AppendRuntime(sb, venueManager, venue);
            AppendLevels(sb, venue);

            sb.AppendLine("  CAND\tRECIPE\tTYPE\tPRICE\tCALORIES\tEXCLUSIVITY\tLOCVALUE\tSCORE\tINGREDIENTS");
            foreach (var recipe in Candidates(venue, recipes, allowed))
            {
                var judged = Judge(recipe, prefs, groups, canPrecalc, canJudge);
                sb.AppendLine($"  CAND\t{recipe.name}\t{S(() => recipe.RecipeType.ToString())}\t{F(judged.Price)}" +
                              $"\t{F(judged.Calories)}\t{F(judged.Exclusivity)}\t{F(judged.LocationValue)}" +
                              $"\t{judged.Score}\t{IngredientSummary(recipe)}");
                rows++;
            }
        }

        var path = Path.Combine(Paths.BepInExRootPath, "nivalis-menus.txt");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        print($"Wrote {venues.Count} venues ({owned.Count} owned), {recipes.Count} recipes, " +
              $"{locationRows} scored rows across {demographics.Count} locations " +
              $"and {rows} candidate rows to {path}");
    }

    /// <summary>
    /// The level-gated caps live on the runtime ghost, not on the authored venue asset. These are
    /// the numbers that decide whether a 15-dish menu is even legal and whether its ingredients fit.
    /// </summary>
    private static void AppendRuntime(StringBuilder sb, VenueManager manager, Venue venue)
    {
        if (manager == null)
            return;

        VenueAreaGhost ghost;
        try { ghost = manager.GetRuntimeData(venue); }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"dumpmenus: no runtime data for {venue.name}: {e.Message}");
            return;
        }

        if (ghost == null)
        {
            sb.AppendLine("  RUNTIME\t(none)");
            return;
        }

        sb.AppendLine($"  RUNTIME\tlevel={S(() => ghost.CurrentLevel.ToString())}" +
                      $"\tmenuLimit={S(() => ghost.MenuLimit.ToString())}" +
                      $"\tstaffLimit={S(() => ghost.StaffLimit.ToString())}" +
                      $"\ttableLimit={S(() => ghost.TableLimit.ToString())}" +
                      $"\tmealsServed={S(() => ghost.MealsServed.ToString())}");
        AppendContainer(sb, "FRIDGE", S2(() => ghost.FridgeInventory));
        AppendContainer(sb, "CUPBOARD", S2(() => ghost.CupboardInventory));
    }

    private static void AppendContainer(StringBuilder sb, string label, ItemContainer container)
    {
        if (container == null)
        {
            sb.AppendLine($"  {label}\t(none)");
            return;
        }

        sb.AppendLine($"  {label}\tcoldCap={Nullable(() => container.RefridgeratedCapacity)}" +
                      $"\tnormalCap={Nullable(() => container.NormalCapacity)}" +
                      $"\tcold={S(() => container.RefridgeratedCount.ToString())}" +
                      $"\tnormal={S(() => container.NormalCount.ToString())}" +
                      $"\tstacks={S(() => container.StackCount.ToString())}" +
                      $"\titems={S(() => container.ItemCount.ToString())}");
    }

    /// <summary>Shows what each level grants, so a menu can be planned against a future level too.</summary>
    private static void AppendLevels(StringBuilder sb, Venue venue)
    {
        var levels = S2(() => venue.LevellingData);
        if (levels == null)
            return;

        for (var i = 0; i < levels.Length; i++)
        {
            var data = levels[i];
            if (data == null)
                continue;
            sb.AppendLine($"  LEVEL\t{i}\tmenu={Opt(() => data.menuRestriction)}" +
                          $"\tfridge={Opt(() => data.numFridgeStorage)}" +
                          $"\tnormal={Opt(() => data.numNormalStorage)}" +
                          $"\ttables={Opt(() => data.tableCountRestriction)}" +
                          $"\tstaff={Opt(() => data.staffCountRestriction)}" +
                          $"\tappliances={Opt(() => data.numAppliances)}" +
                          $"\tmealsServed={Opt(() => data.mealsServed)}" +
                          $"\trecipeUnlock={S(() => data.recipeUnlock?.name)}");
        }
    }

    private static string Opt(Func<Optional<int>> get)
    {
        try
        {
            var o = get();
            return o.HasValue ? o.Value.ToString() : "-";
        }
        catch { return "?"; }
    }

    private static string Nullable(Func<Il2CppSystem.Nullable<int>> get)
    {
        try
        {
            var n = get();
            return n.HasValue ? n.Value.ToString() : "unlimited";
        }
        catch { return "?"; }
    }

    private static List<MealRecipeDefinition> Candidates(
        Venue venue, List<MealRecipeDefinition> all, HashSet<string> allowed)
    {
        // A fixed-menu chain authors its own list; everything else draws on the global pool
        // filtered by the venue's allowed recipe types.
        var authored = S2(() => venue.Recipes);
        if (authored != null && authored.Length > 0)
        {
            var list = new List<MealRecipeDefinition>();
            foreach (var r in authored)
                if (r != null)
                    list.Add(r);
            return list;
        }

        // No authored types is the venue saying it restricts nothing, not that it allows nothing;
        // Doors To Yama has an empty array and serves a salad and a drink side by side.
        if (allowed.Count == 0)
            return all.ToList();

        return all.Where(r =>
        {
            var type = S(() => r.RecipeType.ToString());
            return type != null && allowed.Contains(type);
        }).ToList();
    }

    private static List<Venue> OwnedVenues(PlayerManager.Player player, List<Venue> all)
    {
        if (player == null)
            return all;

        try
        {
            var results = new Il2CppSystem.Collections.Generic.List<Venue>();
            player.GetOwnedVenues(results);
            var list = new List<Venue>();
            for (var i = 0; i < results.Count; i++)
                if (results[i] != null)
                    list.Add(results[i]);
            if (list.Count > 0)
                return list.OrderBy(v => v.name, StringComparer.OrdinalIgnoreCase).ToList();
            Plugin.Instance.Log.LogWarning("dumpmenus: the player owns no venues; exporting all of them.");
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"dumpmenus: GetOwnedVenues failed ({e.Message}); exporting all.");
        }

        return all;
    }

    private static string OwnershipState(PlayerManager.Player player, Venue venue)
    {
        if (player == null)
            return "?";
        try { return player.GetPropertyOwnershipState(venue).ToString(); }
        catch { return "?"; }
    }

    /// <summary>
    /// The venue's own priority list is usually empty, so the location's demographic mix is the
    /// real audience. Each entry carries how much of the local population it represents.
    ///
    /// Which of the two it is matters when reading the numbers: a priority list gives every group
    /// a weight of 1, so a venue with one can score quite differently from its neighbour on the
    /// same street. That is why the source is written into the export.
    /// </summary>
    private static List<(DemographicGroup Group, float Weight)> WeightedGroups(
        Venue venue, LocationDemographics demo, out string source)
    {
        var result = new List<(DemographicGroup, float)>();

        var priority = S2(() => venue.DemographicPriority);
        if (priority != null && priority.Length > 0)
        {
            foreach (var g in priority)
                if (g != null)
                    result.Add((g, 1f));
            source = "venue.DemographicPriority";
            return result;
        }

        result = LocationGroups(demo);
        source = result.Count > 0 ? "location.groups" : "none";
        return result;
    }

    private static List<(DemographicGroup Group, float Weight)> LocationGroups(LocationDemographics demo)
    {
        var result = new List<(DemographicGroup, float)>();
        if (demo == null)
            return result;

        try
        {
            var groups = demo.groups;
            for (var i = 0; i < groups.Count; i++)
            {
                var entry = groups[i];
                var group = entry.group;
                if (group != null)
                    result.Add((group, Math.Max(entry.representation, 0.0001f)));
            }
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"dumpmenus: could not read location groups: {e.Message}");
        }

        return result;
    }

    /// <summary>What the game's own judgement maths makes of one recipe for one audience.</summary>
    private readonly struct Judged
    {
        public readonly float Price;
        public readonly float Calories;
        public readonly float Exclusivity;
        public readonly float LocationValue;
        public readonly string Score;

        public Judged(float price, float calories, float exclusivity, float locationValue, string score)
        {
            Price = price;
            Calories = calories;
            Exclusivity = exclusivity;
            LocationValue = locationValue;
            Score = score;
        }
    }

    /// <summary>
    /// Scores one recipe for one audience by calling the game's own maths, once per demographic
    /// group and weighted by how much of the population that group represents. A group that throws
    /// is dropped from both the total and the divisor, so a partial failure lowers confidence in
    /// the figure rather than silently dragging it towards zero.
    /// </summary>
    private static Judged Judge(MealRecipeDefinition recipe,
                                Il2CppSystem.Collections.Generic.HashSet<int> prefs,
                                List<(DemographicGroup Group, float Weight)> groups,
                                bool canPrecalc, bool canJudge)
    {
        var log = Plugin.Instance.Log;
        float calories = 0f, exclusivity = 0f, locationValue = 0f;
        var precalculated = false;

        if (canPrecalc)
        {
            try
            {
                DemographicGroup.PrecalculateRecipeJudgementInfo(
                    recipe.Recipe, prefs, out calories, out exclusivity, out locationValue);
                precalculated = true;
            }
            catch (Exception e)
            {
                log.LogWarning($"dumpmenus: precalc failed for {recipe.name}: {e.Message}");
            }
        }

        var price = N(() => recipe.CurrentBasePrice) ?? 0f;
        var score = "n/a";

        if (precalculated && canJudge && groups.Count > 0)
        {
            var total = 0f;
            var weight = 0f;
            foreach (var (group, w) in groups)
            {
                try
                {
                    var breakdown = group.CalculateRecipeJudgementBreakdown(
                        recipe.Recipe, price, calories, exclusivity, locationValue);
                    total += breakdown.FinalScore * w;
                    weight += w;
                }
                catch (Exception e)
                {
                    log.LogWarning($"dumpmenus: judge failed for {recipe.name}: {e.Message}");
                }
            }

            if (weight > 0f)
                score = F(total / weight);
        }

        return new Judged(price, calories, exclusivity, locationValue, score);
    }

    private static Il2CppSystem.Collections.Generic.HashSet<int> LocalPreferences(
        LocationDemographics demo, out string names)
    {
        names = "";
        var set = S2(() => demo?.LocalPreferencesHashSet);
        // The raw serialized list is used rather than the IReadOnlyList property, because interop
        // does not surface Count on its read-only collection interfaces.
        var tags = S2(() => demo?.localPreferences);
        if (tags != null)
        {
            var list = new List<string>();
            try
            {
                for (var i = 0; i < tags.Count; i++)
                    list.Add(AssetName(tags[i]));
            }
            catch (Exception e)
            {
                Plugin.Instance.Log.LogWarning($"dumpmenus: could not read local preferences: {e.Message}");
            }

            names = string.Join(",", list);
        }

        // Passing null into the game's precalc is not worth finding out what it does.
        return set ?? new Il2CppSystem.Collections.Generic.HashSet<int>();
    }

    private static string CurrentMenu(Venue venue)
    {
        var menu = S2(() => venue.CurrentMealMenu);
        if (menu == null)
            return "";
        var names = new List<string>();
        foreach (var entry in menu)
        {
            var name = S(() => entry?.RecipeDefinition?.name);
            if (name != null)
                names.Add($"{name}@{S(() => entry.Price.ToString())}");
        }

        return string.Join(";", names);
    }

    private static HashSet<string> AllowedTypeSet(Venue venue)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var types = S2(() => venue.RecipeType);
        if (types == null)
            return set;
        foreach (var t in types)
            set.Add(t.ToString());
        return set;
    }

    private static string AllowedTypes(Venue venue) => string.Join(",", AllowedTypeSet(venue));

    private static string LocationName(Venue venue) => S(() => venue.Location?.name) ?? "?";

    private static string TagNames(MealRecipeDefinition.InputDefinition input)
    {
        var tags = S2(() => input.AllowedTags);
        if (tags == null)
            return "";
        var names = new List<string>();
        foreach (var tag in tags)
            if (tag != null)
                names.Add(AssetName(tag));
        return string.Join(",", names);
    }

    /// <summary>ObjectTag declares its own 'name' for localisation, which hides the asset name.</summary>
    private static string AssetName(ObjectTag tag) => ((UnityEngine.Object)tag)?.name ?? "?";

    private static string SlotCount(MealRecipeDefinition recipe) =>
        S(() => recipe.Inputs?.Length.ToString()) ?? "0";

    private static string CustomSlotCount(MealRecipeDefinition recipe)
    {
        var inputs = S2(() => recipe.Inputs);
        if (inputs == null)
            return "0";
        var n = 0;
        foreach (var input in inputs)
            if (input != null && (S(() => input.IsCustomizable.ToString()) == "True"))
                n++;
        return n.ToString();
    }

    /// <summary>Distinct default ingredients, which is what a fridge actually has to hold.</summary>
    private static string IngredientSummary(MealRecipeDefinition recipe)
    {
        var inputs = S2(() => recipe.Inputs);
        if (inputs == null)
            return "";
        var names = new List<string>();
        foreach (var input in inputs)
        {
            var name = S(() => input?.DefaultItem?.name);
            if (name != null && !names.Contains(name))
                names.Add(name);
        }

        return string.Join(";", names);
    }

    private static List<T> All<T>() where T : UnityEngine.Object
    {
        var found = new List<T>();
        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
        {
            var cast = obj?.TryCast<T>();
            if (cast != null)
                found.Add(cast);
        }

        return found.OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string F(float? value) => value.HasValue ? value.Value.ToString("0.###") : "n/a";
    private static string F(float value) => value.ToString("0.###");

    private static string S(Func<string> get)
    {
        try { return get(); }
        catch { return null; }
    }

    private static T S2<T>(Func<T> get) where T : class
    {
        try { return get(); }
        catch { return null; }
    }

    private static float? N(Func<float> get)
    {
        try { return get(); }
        catch { return null; }
    }
}
