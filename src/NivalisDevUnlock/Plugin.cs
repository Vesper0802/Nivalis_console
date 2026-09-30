using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Nivalis.DevOptions;
using Nivalis.UI;
using UnityEngine;

namespace NivalisDevUnlock;

[BepInPlugin(Guid, "Nivalis Nights Dev Unlock", "0.1.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "nivalisnights.devunlock";

    internal static Plugin Instance;
    internal static ConfigEntry<KeyCode> OpenMenuKey;
    internal static ConfigEntry<KeyCode> ListOptionsKey;

    public override void Load()
    {
        Instance = this;

        OpenMenuKey = Config.Bind("Hotkeys", "OpenMenu", KeyCode.F1,
            "Opens the game's built-in developer option menu.");
        ListOptionsKey = Config.Bind("Hotkeys", "ListOptions", KeyCode.F2,
            "Writes every discovered dev option to the BepInEx log.");

        // DevMode.IsDevMode is a lazy getter over a Nullable<bool> that is filled by
        // RSA-verifying the `token` field in settings.ini. Short-circuit the getter so
        // the token is never consulted. This has to happen before the first scene loads,
        // because DevOptionMenuRevised.Awake() reads it exactly once.
        var harmony = new Harmony(Guid);
        Force(harmony, typeof(DevMode), "get_IsDevMode");
        Force(harmony, typeof(BuildVersionData), "get_AreDevOptionsEnabled");

        ClassInjector.RegisterTypeInIl2Cpp<DevUnlockBehaviour>();
        var host = new GameObject(nameof(DevUnlockBehaviour));
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<DevUnlockBehaviour>();

        Log.LogInfo($"Dev mode forced on. {OpenMenuKey.Value} opens the dev menu, " +
                    $"{ListOptionsKey.Value} lists every dev option.");
    }

    private void Force(Harmony harmony, System.Type type, string getter)
    {
        var target = AccessTools.Method(type, getter);
        if (target == null)
        {
            Log.LogError($"Could not find {type.FullName}.{getter} — the game probably updated.");
            return;
        }

        harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(ReturnTrue))));
        Log.LogInfo($"Patched {type.Name}.{getter}");
    }

    private static bool ReturnTrue(ref bool __result)
    {
        __result = true;
        return false;
    }
}
