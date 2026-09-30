using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NivalisDevUnlock;

/// <summary>
/// Suspends the game's own input while the console is open, so typing a command does not
/// also drive the player (typing "items" would otherwise fire the E interact action).
///
/// This disables InputActionMaps rather than the keyboard device, because the console reads
/// Keyboard.current directly and would lock itself out along with the game.
/// </summary>
internal static class GameInput
{
    private static readonly List<InputActionMap> Suspended = new();

    public static void Suspend()
    {
        Resume();

        var assets = Resources.FindObjectsOfTypeAll(Il2CppType.Of<InputActionAsset>());
        foreach (var obj in assets)
        {
            var asset = obj?.TryCast<InputActionAsset>();
            var maps = asset?.m_ActionMaps;
            if (maps == null)
                continue;

            foreach (var map in maps)
            {
                if (map == null || !map.enabled)
                    continue;

                try
                {
                    map.Disable();
                    Suspended.Add(map);
                }
                catch (Exception e)
                {
                    Plugin.Instance?.Log.LogWarning($"Could not disable an action map: {e.Message}");
                }
            }
        }
    }

    /// <summary>Re-enables exactly the maps that were enabled before suspending.</summary>
    public static void Resume()
    {
        foreach (var map in Suspended)
        {
            try { map?.Enable(); }
            catch (Exception e) { Plugin.Instance?.Log.LogWarning($"Could not re-enable an action map: {e.Message}"); }
        }

        Suspended.Clear();
    }

    public static int SuspendedCount => Suspended.Count;
}
