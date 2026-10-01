using HarmonyLib;
using Nivalis.InventorySystem;

namespace NivalisDevUnlock;

/// <summary>
/// Stops food from ageing. ItemInstanceData.UpdateDecay is the only place a single item's
/// remainingDecayTime moves, so skipping it covers the player inventory, venue storage and
/// fridges at once — the container-level UpdateItemDecay walkers all funnel into it.
/// </summary>
internal static class DecayFreeze
{
    public static bool Enabled;
    public static bool Patched { get; private set; }

    public static void Apply(Harmony harmony)
    {
        var target = AccessTools.Method(typeof(ItemInstanceData), nameof(ItemInstanceData.UpdateDecay));
        if (target == null)
        {
            Plugin.Instance.Log.LogError("Could not find ItemInstanceData.UpdateDecay — the game probably updated.");
            return;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(DecayFreeze), nameof(Prefix))));
        Patched = true;
    }

    private static bool Prefix() => !Enabled;
}
