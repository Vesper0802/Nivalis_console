using HarmonyLib;
using Nivalis;
using UnityEngine;

namespace NivalisDevUnlock;

/// <summary>
/// Creates the console's host object once Unity is able to make one.
/// Doing it in Plugin.Load() throws inside il2cpp, and BepInEx's AddComponent helper needs an
/// AOT-compiled generic FindObjectsOfType that does not exist for an injected type, so the
/// creation is instead triggered from the first game code that runs after Unity is alive.
/// </summary>
internal static class ConsoleHost
{
    private static bool _created;
    private static int _failures;

    public static void Arm(Harmony harmony)
    {
        var log = Plugin.Instance.Log;
        // Unity invokes Awake and Update through the method pointer it captured at registration,
        // so a Harmony detour on those never runs. Only methods the game's own managed code
        // calls are reachable, which is what these are.
        var hooks = new (Type Type, string Method)[]
        {
            (typeof(GameSceneManager), "FinalizeManagerInitialization"),
            (typeof(GameSceneManager), "GameplayInternalInitializationDoneListener"),
            (typeof(GameSceneManager), "StartLoadedGame"),
        };

        var armed = 0;
        foreach (var (type, method) in hooks)
        {
            if (!Interop.HasNativeMethod(type, method))
            {
                log.LogWarning($"{type.Name}.{method} has no native pointer; not using it to start the console.");
                continue;
            }

            var target = AccessTools.Method(type, method);
            if (target == null)
            {
                log.LogWarning($"{type.Name}.{method} not found; not using it to start the console.");
                continue;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(ConsoleHost), nameof(Postfix))));
            armed++;
            log.LogInfo($"Console will start from {type.Name}.{method}.");
        }

        // The dev-mode getters also call Ensure, so the console still appears without these.
        if (armed == 0)
            log.LogWarning("No scene hook attached; the console will start from the dev-mode getters instead.");
    }

    private static void Postfix() => Ensure();

    public static void Ensure()
    {
        if (_created)
            return;
        _created = true;

        try
        {
            var host = new GameObject(nameof(ConsoleWindow));
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<ConsoleWindow>();
            Plugin.Instance.Log.LogInfo(
                $"Console created. Press {Plugin.ToggleKey.Value} in game to open it.");
        }
        catch (Exception e)
        {
            // A retry on the next trigger is worth more than one failed attempt at a bad moment.
            // Triggers fire often, so only the first few failures are worth logging.
            _created = false;
            if (++_failures <= 3)
                Plugin.Instance.Log.LogWarning($"Console creation attempt {_failures} failed, will retry: {e.Message}");
        }
    }
}
