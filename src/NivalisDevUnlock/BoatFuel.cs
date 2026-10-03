using Nivalis;
using Nivalis.Boat;
using Nivalis.SkillSystem;

namespace NivalisDevUnlock;

/// <summary>
/// Fills the boat's tank. Fuel is held on the boat's ghost rather than on the view, so this works
/// whether or not the boat is in the loaded scene, which is when a trip to a fuel station is
/// least convenient.
/// </summary>
internal static class BoatFuel
{
    public static void Run(Action<string> print)
    {
        var boat = Boat(print);
        if (boat == null)
            return;

        var capacity = Capacity(out var source);
        if (capacity == null)
        {
            print("Could not work out the tank size, so there is nothing to fill to. " +
                  "Try again near the boat.");
            return;
        }

        float before;
        try { before = boat.Fuel; }
        catch (Exception e)
        {
            print($"Could not read the boat's fuel: {e.Message}");
            return;
        }

        if (before >= capacity.Value)
        {
            print($"The tank is already full: {before:0.#} of {capacity.Value:0.#}.");
            return;
        }

        try { boat.Fuel = capacity.Value; }
        catch (Exception e)
        {
            print($"Could not set the boat's fuel: {e.Message}");
            return;
        }

        print($"Boat fuel {before:0.#} -> {Read(boat):0.#} of {capacity.Value:0.#} " +
              $"(tank size from {source}). No charge.");
    }

    private static BoatGhost Boat(Action<string> print)
    {
        try
        {
            var found = BoatGhost.FindBoat();
            if (found != null)
                return found;
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"refuel: FindBoat failed: {e.Message}");
        }

        // The ghost is the better source, but the loaded view holds one too.
        try
        {
            var view = BoatController.Instance;
            if (view != null && view.MyGhost != null)
                return view.MyGhost;
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"refuel: BoatController.Instance failed: {e.Message}");
        }

        print("No boat found. Run 'unlockboat' first if you do not have one yet.");
        return null;
    }

    /// <summary>
    /// The loaded boat knows its own tank size. When it is not loaded the size still follows the
    /// Boat skill, which is where the boat reads it from, so the level data stands in.
    /// </summary>
    private static float? Capacity(out string source)
    {
        source = "the boat";
        try
        {
            var view = BoatController.Instance;
            if (view != null)
            {
                var capacity = view.FuelCapacity;
                if (capacity > 0f)
                    return capacity;
            }
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"refuel: FuelCapacity failed: {e.Message}");
        }

        return FromSkill(out source);
    }

    private static float? FromSkill(out string source)
    {
        source = "your Boat skill";
        var controller = GameRefs.Find<SkillLevelController>();
        if (controller == null)
            return null;

        try
        {
            var skills = controller._allSkills;
            if (skills == null)
                return null;

            for (var i = 0; i < skills.Length; i++)
            {
                var skill = skills[i];
                var levels = skill?._levelData;
                if (levels == null || levels.Length == 0)
                    continue;

                // Interop models the il2cpp hierarchy as classes, so the boat levels are
                // recognised by a runtime cast rather than by the asset's name.
                if (levels[0].TryCast<BoatLevel>() == null)
                    continue;

                var level = controller.GetPlayerSkillExperience(skill).CurrentLevel;
                for (var j = 0; j < levels.Length; j++)
                {
                    var boatLevel = levels[j].TryCast<BoatLevel>();
                    if (boatLevel == null || levels[j].LevelNumber != level)
                        continue;

                    source = $"your Boat skill at level {level}";
                    return boatLevel.FuelCapacity;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Instance.Log.LogWarning($"refuel: could not read the Boat skill: {e.Message}");
        }

        return null;
    }

    private static float Read(BoatGhost boat)
    {
        try { return boat.Fuel; }
        catch { return 0f; }
    }
}
