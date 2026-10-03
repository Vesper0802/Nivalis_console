using Nivalis;
using Nivalis.SkillSystem;

namespace NivalisDevUnlock;

/// <summary>
/// Reads and raises the player's skill levels. The game awards these through experience only and
/// exposes no dev option for them, so unlike most of this console's commands there is nothing to
/// wrap and the levels have to be driven by granting experience.
/// </summary>
internal static class Skills
{
    public static void Run(string[] args, Action<string> print)
    {
        var controller = GameRefs.Find<SkillLevelController>();
        var player = GameRefs.Find<PlayerManager>()?.LocalPlayer;
        if (controller == null || player == null)
        {
            print("No SkillLevelController or player in the scene — load a save first.");
            return;
        }

        var skills = All(controller, print);
        if (skills == null)
            return;

        if (args.Length == 0)
        {
            foreach (var skill in skills)
                print("  " + Describe(controller, skill));
            print("Use 'skill <name> <level>' to raise one.");
            return;
        }

        // A name with no level shows that skill's level table, which is what the arithmetic for
        // raising a level rests on and is otherwise invisible.
        var hasTarget = int.TryParse(args[^1], out var target);
        var name = string.Join(" ", hasTarget ? args[..^1] : args);
        if (string.IsNullOrWhiteSpace(name))
        {
            print("Usage: skill <name> [level]. Run 'skill' on its own for the list.");
            return;
        }

        var matches = skills.Where(s => Matches(s, name)).ToList();

        if (matches.Count != 1)
        {
            print(matches.Count == 0
                ? $"No skill matches '{name}'. The skills are:"
                : $"'{name}' matches several skills:");
            foreach (var skill in matches.Count == 0 ? skills : matches)
                print("  " + Describe(controller, skill));
            return;
        }

        if (!hasTarget)
        {
            Detail(controller, matches[0], print);
            return;
        }

        Raise(controller, player, matches[0], target, print);
    }

    /// <summary>
    /// The authored cost of each level next to the experience actually held, which is the only
    /// way to see whether the stored figure counts from zero or from the current level.
    /// </summary>
    private static void Detail(SkillLevelController controller, SkillDefinition skill, Action<string> print)
    {
        print(Describe(controller, skill));

        var levels = LevelData(skill);
        if (levels.Count == 0)
        {
            print("  The game exposes no level data for this skill.");
            return;
        }

        var running = 0f;
        foreach (var level in levels)
        {
            var cost = Cost(level);
            running += cost;
            print($"  level {Number(level)}: costs {cost:0.###}, running total {running:0.###}, " +
                  $"speed {Speed(level):0.###}");
        }
    }

    /// <summary>
    /// Experience is a running total from zero, and a level is held once that total reaches the
    /// level's share of RelativeExperienceRequired, so reaching a level is a single top-up. The
    /// result is read back anyway, because the game decides when to re-evaluate the level.
    /// </summary>
    private static void Raise(SkillLevelController controller, PlayerManager.Player player,
        SkillDefinition skill, int target, Action<string> print)
    {
        if (IsStaffSkill(skill))
        {
            print($"{Id(skill)} is a staff skill, not one of yours — it belongs to an employee.");
            return;
        }

        var thresholds = Thresholds(skill);
        if (!thresholds.TryGetValue(target, out var required))
        {
            print(thresholds.Count == 0
                ? $"{Id(skill)} exposes no level data, so its levels cannot be set."
                : $"{Id(skill)} has levels {thresholds.Keys.Min()} to {thresholds.Keys.Max()}.");
            return;
        }

        var start = Level(controller, skill);
        if (start >= target)
        {
            print($"{Id(skill)} is already level {start}.");
            return;
        }

        var have = Experience(controller, skill);

        // A whole point over the threshold costs nothing and keeps float rounding from landing
        // just under it; the next level is hundreds of thousands away.
        var top_up = required - have + 1f;

        try
        {
            player.AddExperience(skill, top_up);
        }
        catch (Exception e)
        {
            print($"Could not add experience to {Id(skill)}: {e.Message}");
            return;
        }

        var now = Level(controller, skill);
        var message = $"{Id(skill)}: level {start} -> {now}, {top_up:0} xp added " +
                      $"({Experience(controller, skill):0} total).";
        print(now == target ? message : message + $" Level {target} was asked for.");
    }

    /// <summary>
    /// The experience total each level calls for, keyed by the level numbers the game itself
    /// uses. Those start at zero, so a level is not its position in the array.
    /// </summary>
    private static Dictionary<int, float> Thresholds(SkillDefinition skill)
    {
        var totals = new Dictionary<int, float>();
        var running = 0f;

        foreach (var level in LevelData(skill))
        {
            running += Cost(level);
            totals[Number(level)] = running;
        }

        return totals;
    }

    /// <summary>Matches the English name that gets typed, or the localised one that is on screen.</summary>
    private static bool Matches(SkillDefinition skill, string query) =>
        Id(skill).Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (Localised(skill)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string Describe(SkillLevelController controller, SkillDefinition skill)
    {
        var level = Level(controller, skill);
        var have = Experience(controller, skill);
        var localised = Localised(skill);
        var owner = IsStaffSkill(skill) ? "  (staff)" : "";

        // The top level is the highest number the game authored, which is one less than the count
        // because the levels start at zero.
        var thresholds = Thresholds(skill);
        var top = thresholds.Count > 0 ? thresholds.Keys.Max().ToString() : "?";

        return $"{Id(skill)}{(localised == null ? "" : $" [{localised}]")}" +
               $": level {level}/{top}, {have:0.#} xp{owner}";
    }

    private static List<SkillDefinition> All(SkillLevelController controller, Action<string> print)
    {
        try
        {
            // The backing array is read instead of AllSkills, because interop's generic
            // enumerators are the part of this API that has taken the process down before.
            var raw = controller._allSkills;
            if (raw == null || raw.Length == 0)
            {
                print("The game reports no skills at all.");
                return null;
            }

            var skills = new List<SkillDefinition>();
            for (var i = 0; i < raw.Length; i++)
                if (raw[i] != null)
                    skills.Add(raw[i]);

            return skills.OrderBy(Id, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e)
        {
            print($"Could not list the skills: {e.Message}");
            return null;
        }
    }

    private static List<SkillLevelData> LevelData(SkillDefinition skill)
    {
        var levels = new List<SkillLevelData>();
        try
        {
            var raw = skill._levelData;
            if (raw != null)
                for (var i = 0; i < raw.Length; i++)
                    if (raw[i] != null)
                        levels.Add(raw[i]);
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"skill: could not read levels of {Id(skill)}: {e.Message}");
        }

        return levels.OrderBy(l => Number(l)).ToList();
    }

    private static int Number(SkillLevelData level)
    {
        try { return level.LevelNumber; }
        catch { return 0; }
    }

    private static float Cost(SkillLevelData level)
    {
        try { return level.RelativeExperienceRequired; }
        catch { return 0f; }
    }

    private static float Speed(SkillLevelData level)
    {
        try { return level.Speed; }
        catch { return 0f; }
    }

    /// <summary>Staff skills share the system but are read off an employee, not off the player.</summary>
    private static bool IsStaffSkill(SkillDefinition skill)
    {
        var levels = LevelData(skill);
        if (levels.Count == 0)
            return false;

        // Interop models the il2cpp hierarchy as classes, so this needs a runtime cast.
        try { return levels[0].TryCast<StaffSkillLevelData>() != null; }
        catch { return false; }
    }

    private static int Level(SkillLevelController controller, SkillDefinition skill)
    {
        try { return controller.GetPlayerSkillExperience(skill).CurrentLevel; }
        catch { return 0; }
    }

    private static float Experience(SkillLevelController controller, SkillDefinition skill)
    {
        try { return controller.GetPlayerSkillExperience(skill).Experience; }
        catch { return 0f; }
    }

    /// <summary>
    /// The asset name, which stays English whatever language the game is running in, so a command
    /// can be typed and shared regardless of locale. DisplayName is localised and only displayed.
    /// </summary>
    private static string Id(SkillDefinition skill)
    {
        try
        {
            var name = ((UnityEngine.Object)skill).name;
            if (!string.IsNullOrWhiteSpace(name))
                return Shorten(name);
        }
        catch { }

        return "?";
    }

    /// <summary>The assets are named after their type, which reads as a mouthful on its own.</summary>
    private static string Shorten(string name)
    {
        foreach (var suffix in new[] { "SkillLevels", "SkillLevel", "Levels", "Skill" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];

        return name;
    }

    /// <summary>The name as the game's UI shows it, for matching up with what is on screen.</summary>
    private static string Localised(SkillDefinition skill)
    {
        try
        {
            var display = skill.DisplayName;
            return string.IsNullOrWhiteSpace(display) ? null : display;
        }
        catch { return null; }
    }
}
