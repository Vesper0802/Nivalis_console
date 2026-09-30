using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Nivalis.DevOptions;
using Nivalis.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisDevUnlock;

[BepInPlugin(Guid, "Nivalis Nights Dev Unlock", "0.2.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "nivalisnights.devunlock";

    internal static Plugin Instance;
    internal static ConfigEntry<Key> ToggleKey;
    internal static ConfigEntry<bool> PauseWhileOpen;
    internal static ConfigEntry<bool> BlockGameInput;
    internal static ConfigEntry<float> UiScale;

    public override void Load()
    {
        Instance = this;

        ToggleKey = Config.Bind("Console", "ToggleKey", Key.F1,
            "Key that opens and closes the console. Uses Input System key names.");
        PauseWhileOpen = Config.Bind("Console", "PauseWhileOpen", true,
            "Sets Time.timeScale to 0 while the console is open. This also stops the " +
            "player from walking around as you type, since movement is time-scaled.");
        BlockGameInput = Config.Bind("Console", "BlockGameInput", true,
            "Disables the game's input action maps while the console is open, so typing a " +
            "command does not also trigger interactions like E.");
        UiScale = Config.Bind("Console", "UiScale", 2f,
            "Magnification of the console overlay. Applied through GUI.matrix, so the " +
            "whole panel scales rather than just the text.");

        // DevMode.IsDevMode is a lazy getter over a Nullable<bool> that is filled by
        // RSA-verifying the `token` field in settings.ini. Short-circuit the getter so
        // the token is never consulted. This has to happen before the first scene loads,
        // because DevOptionMenuRevised.Awake() reads it exactly once.
        var harmony = new Harmony(Guid);
        Force(harmony, typeof(DevMode), "get_IsDevMode");
        Force(harmony, typeof(BuildVersionData), "get_AreDevOptionsEnabled");

        ClassInjector.RegisterTypeInIl2Cpp<ConsoleWindow>();
        var host = new GameObject(nameof(ConsoleWindow));
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<ConsoleWindow>();

        Log.LogInfo($"Dev mode forced on. Press {ToggleKey.Value} in game to open the console.");
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
