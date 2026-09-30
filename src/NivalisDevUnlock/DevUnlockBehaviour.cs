using BepInEx.Logging;
using Nivalis.DevOptions;
using UnityEngine;

namespace NivalisDevUnlock;

/// <summary>
/// Drives the hotkeys. Injected into IL2CPP so Unity will call Update on it.
/// </summary>
public class DevUnlockBehaviour : MonoBehaviour
{
    private static ManualLogSource Log => Plugin.Instance.Log;

    private bool _legacyInputBroken;

    public DevUnlockBehaviour(System.IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        if (_legacyInputBroken)
            return;

        try
        {
            if (Input.GetKeyDown(Plugin.OpenMenuKey.Value))
                OpenMenu();
            else if (Input.GetKeyDown(Plugin.ListOptionsKey.Value))
                ListOptions();
        }
        catch (System.Exception e)
        {
            // Happens if the build is set to "Input System (New)" only, in which case
            // UnityEngine.Input throws instead of returning false.
            _legacyInputBroken = true;
            Log.LogError($"Legacy input unavailable, hotkeys disabled: {e.Message}");
        }
    }

    private static void OpenMenu()
    {
        var menu = DevOptionMenuRevised.Instance;
        if (menu == null)
        {
            Log.LogWarning("DevOptionMenuRevised.Instance is null — the menu UI has not been " +
                           "created yet. Get past the logo/main menu and try again.");
            return;
        }

        menu.Open();
        Log.LogInfo("Opened the dev option menu.");
    }

    private static void ListOptions()
    {
        var menu = DevOptionMenuRevised.Instance;
        if (menu == null)
        {
            Log.LogWarning("DevOptionMenuRevised.Instance is null — nothing to list yet.");
            return;
        }

        var attributes = DevOptionMenuRevised._methodDisplayString;
        if (attributes == null)
        {
            Log.LogWarning("Dev options have not been discovered yet.");
            return;
        }

        Log.LogInfo($"{attributes.Length} dev options (index is what InvokeMethod takes):");
        for (var i = 0; i < attributes.Length; i++)
        {
            var attr = attributes[i];
            if (attr == null)
                continue;

            var shortcut = attr.Shortcut == KeyCode.None ? "" : $"  [{attr.Shortcut}]";
            var editorOnly = attr.OnlyInEditor ? "  (editor only)" : "";
            var index = i.ToString().PadLeft(3);
            Log.LogInfo($"  {index}  {attr.Name}{shortcut}{editorOnly}");
        }
    }
}
