using HarmonyLib;
using Nivalis.InventorySystem;

namespace NivalisDevUnlock;

/// <summary>
/// Stops food from ageing.
///
/// ItemInstanceData.UpdateDecay is the ideal hook — every container-level decay walker funnels
/// into it — but most of that class failed to bind through interop in this build, so patching it
/// detoured onto a null pointer and took the process down. Each candidate is therefore checked
/// for real native code first, working outwards from the leaf to the containers.
/// </summary>
internal static class DecayFreeze
{
    public static bool Enabled;
    public static bool Patched { get; private set; }
    public static string Hook { get; private set; } = "none";

    public static void Apply(Harmony harmony)
    {
        var log = Plugin.Instance.Log;
        var candidates = new (Type Type, string Method)[]
        {
            (typeof(ItemInstanceData), nameof(ItemInstanceData.UpdateDecay)),
            (typeof(ItemStack), nameof(ItemStack.UpdateItemDecay)),
            (typeof(ItemContainer), nameof(ItemContainer.UpdateItemDecay)),
        };

        foreach (var (type, method) in candidates)
        {
            if (!Interop.HasNativeMethod(type, method))
            {
                log.LogWarning($"{type.Name}.{method} has no native pointer; not patching it.");
                continue;
            }

            var target = AccessTools.Method(type, method);
            if (target == null)
            {
                log.LogWarning($"AccessTools could not resolve {type.Name}.{method}.");
                continue;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(DecayFreeze), nameof(Prefix))));
            Patched = true;
            Hook = $"{type.Name}.{method}";
            log.LogInfo($"Decay hook attached to {Hook}.");
            return;
        }

        log.LogError("No decay method could be hooked safely; 'nospoil' will not work.");
    }

    private static bool Prefix() => !Enabled;
}
