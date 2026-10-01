using System.Reflection;

namespace NivalisDevUnlock;

/// <summary>
/// Guards against patching a method the interop layer never bound.
///
/// Il2CppInterop generates a static NativeMethodInfoPtr_ field per method and fills it from
/// il2cpp metadata at type initialisation. When that lookup fails the field stays zero and the
/// log says "Unable to find method". Handing such a method to Harmony produces a detour onto
/// address zero, which is not an error anyone can catch — the process dies with an access
/// violation inside coreclr the moment the game calls it.
/// </summary>
internal static class Interop
{
    /// <summary>
    /// True when at least one overload of <paramref name="methodName"/> resolved to real native
    /// code. Reading the field triggers the type initialiser, so this reflects the final state.
    /// </summary>
    public static bool HasNativeMethod(Type interopType, string methodName)
    {
        var prefix = $"NativeMethodInfoPtr_{methodName}_";
        var fields = interopType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var seen = false;

        foreach (var field in fields)
        {
            if (field.FieldType != typeof(IntPtr) || !field.Name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            seen = true;
            try
            {
                if ((IntPtr)field.GetValue(null) != IntPtr.Zero)
                    return true;
            }
            catch (Exception e)
            {
                Plugin.Instance?.Log.LogWarning($"Could not read {field.Name}: {e.Message}");
            }
        }

        if (!seen)
            Plugin.Instance?.Log.LogWarning($"{interopType.Name} has no NativeMethodInfoPtr for {methodName}.");

        return false;
    }
}
