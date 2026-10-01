using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSystem.Reflection;
using Nivalis.DevOptions;
using UnityEngine;
using Il2CppType = Il2CppInterop.Runtime.Il2CppType;

namespace NivalisDevUnlock;

/// <summary>
/// Bridges the console to the 55 options the developers left behind the [DevOption] attribute.
///
/// The game discovers them at startup into two parallel static arrays on DevOptionMenuRevised:
/// _methodDisplayString holds the attributes (the labels shown in its own UI) and _methods holds
/// (declaringType, methodInfo) pairs. Invoking through Il2Cpp reflection rather than the menu's
/// own InvokeMethod lets us supply parameters from typed text instead of its parameter editors.
/// </summary>
internal static class DevOptions
{
    private static Il2CppReferenceArray<DevOptionAttribute> Attributes => DevOptionMenuRevised._methodDisplayString;

    private static Il2CppReferenceArray<Il2CppSystem.ValueTuple<Il2CppSystem.Type, MethodInfo>> Methods =>
        DevOptionMenuRevised._methods;

    public static int Count => Attributes?.Length ?? 0;

    public static bool Ready => Attributes != null && Methods != null;

    public static string Name(int index) => Attributes?[index]?.Name ?? "?";

    public static MethodInfo Method(int index)
    {
        var methods = Methods;
        return methods == null || index >= methods.Length ? null : methods[index].Item2;
    }

    public static Il2CppSystem.Type DeclaringType(int index)
    {
        var methods = Methods;
        return methods == null || index >= methods.Length ? null : methods[index].Item1;
    }

    /// <summary>
    /// Resolves a dev option by the label the developers gave it. Named lookup keeps the
    /// friendly commands working if a game update reorders the discovery list. The argument
    /// count disambiguates labels that appear twice with different signatures.
    /// </summary>
    public static int FindByName(string label, int argCount)
    {
        var fallback = -1;
        for (var i = 0; i < Count; i++)
        {
            if (!string.Equals(Name(i), label, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Method(i)?.GetParameters()?.Length == argCount)
                return i;

            if (fallback < 0)
                fallback = i;
        }

        return fallback;
    }

    /// <summary>Renders a parameter list the user can read, e.g. "(Int32 level, Single speed)".</summary>
    public static string Signature(int index)
    {
        var method = Method(index);
        if (method == null)
            return "(?)";

        var parameters = method.GetParameters();
        if (parameters == null || parameters.Length == 0)
            return "()";

        var parts = new List<string>();
        foreach (var parameter in parameters)
            parts.Add($"{ShortName(parameter.ParameterType)} {parameter.Name}");

        return "(" + string.Join(", ", parts) + ")";
    }

    public static void Invoke(int index, string[] args, Action<string> print)
    {
        var method = Method(index);
        if (method == null)
        {
            print($"Dev option {index} has no method info.");
            return;
        }

        var parameters = method.GetParameters() ?? new Il2CppReferenceArray<ParameterInfo>(0);
        if (args.Length < parameters.Length)
        {
            print($"{Name(index)} needs {Signature(index)}");
            return;
        }

        var boxed = new Il2CppReferenceArray<Il2CppSystem.Object>(parameters.Length);
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];

            // Weather presets and the like are named assets, so the last textual parameter
            // swallows the rest of the line instead of forcing the user to avoid spaces.
            var raw = i == parameters.Length - 1 && args.Length > parameters.Length
                ? string.Join(" ", args[i..])
                : args[i];

            if (!TryConvert(raw, parameter.ParameterType, out var value, out var problem))
            {
                print($"Parameter '{parameter.Name}': {problem}");
                return;
            }

            boxed[i] = value;
        }

        Il2CppSystem.Object target = null;
        if (!method.IsStatic)
        {
            target = FindTarget(DeclaringType(index));
            if (target == null)
            {
                print($"No live instance of {ShortName(DeclaringType(index))} — load a save first.");
                return;
            }
        }

        var result = method.Invoke(target, boxed);
        print(result == null
            ? $"Ran '{Name(index)}'."
            : $"Ran '{Name(index)}' -> {result}");
    }

    private static bool TryConvert(string raw, Il2CppSystem.Type type, out Il2CppSystem.Object value, out string problem)
    {
        value = null;
        problem = null;
        var name = type?.FullName ?? "";

        switch (name)
        {
            case "System.Int32":
                if (!int.TryParse(raw, out var i)) { problem = $"'{raw}' is not a whole number."; return false; }
                value = new Il2CppSystem.Int32 { m_value = i }.BoxIl2CppObject();
                return true;

            case "System.Single":
                if (!float.TryParse(raw, out var f)) { problem = $"'{raw}' is not a number."; return false; }
                value = new Il2CppSystem.Single { m_value = f }.BoxIl2CppObject();
                return true;

            case "System.Boolean":
                var truthy = raw is "1" or "true" or "True" or "on" or "yes";
                var falsy = raw is "0" or "false" or "False" or "off" or "no";
                if (!truthy && !falsy) { problem = $"'{raw}' is not true/false."; return false; }
                value = new Il2CppSystem.Boolean { m_value = truthy }.BoxIl2CppObject();
                return true;

            case "System.String":
                value = new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(raw));
                return true;
        }

        if (type == null)
        {
            problem = "unknown parameter type.";
            return false;
        }

        if (type.IsEnum)
        {
            try
            {
                value = int.TryParse(raw, out var ordinal)
                    ? Il2CppSystem.Enum.ToObject(type, ordinal)
                    : Il2CppSystem.Enum.Parse(type, raw, true);
                return true;
            }
            catch
            {
                problem = $"'{raw}' is not one of {string.Join(", ", EnumNames(type))}";
                return false;
            }
        }

        if (type.IsSubclassOf(Il2CppType.Of<UnityEngine.Object>()))
            return TryFindAsset(raw, type, out value, out problem);

        problem = $"type {ShortName(type)} is not supported from text — use the game's own dev menu ('devmenu').";
        return false;
    }

    /// <summary>Resolves an asset parameter (weather preset, location, item type) by name.</summary>
    private static bool TryFindAsset(string raw, Il2CppSystem.Type type, out Il2CppSystem.Object value, out string problem)
    {
        value = null;
        problem = null;

        var candidates = Resources.FindObjectsOfTypeAll(type);
        var exact = new List<UnityEngine.Object>();
        var partial = new List<UnityEngine.Object>();

        foreach (var candidate in candidates)
        {
            if (candidate == null)
                continue;

            var assetName = candidate.name ?? "";
            if (string.Equals(assetName, raw, StringComparison.OrdinalIgnoreCase))
                exact.Add(candidate);
            else if (assetName.Contains(raw, StringComparison.OrdinalIgnoreCase))
                partial.Add(candidate);
        }

        var matches = exact.Count > 0 ? exact : partial;
        if (matches.Count == 0)
        {
            var available = new List<string>();
            foreach (var candidate in candidates)
            {
                if (candidate != null && available.Count < 12)
                    available.Add(candidate.name);
            }

            problem = available.Count == 0
                ? $"no {ShortName(type)} named '{raw}' is loaded."
                : $"no {ShortName(type)} matches '{raw}'. Loaded: {string.Join(", ", available)}";
            return false;
        }

        if (matches.Count > 1)
        {
            var names = new List<string>();
            foreach (var match in matches)
                names.Add(match.name);
            problem = $"'{raw}' is ambiguous: {string.Join(", ", names)}";
            return false;
        }

        value = new Il2CppSystem.Object(matches[0].Pointer);
        return true;
    }

    private static Il2CppSystem.Object FindTarget(Il2CppSystem.Type type)
    {
        if (type == null)
            return null;

        var active = UnityEngine.Object.FindObjectOfType(type);
        if (active != null)
            return new Il2CppSystem.Object(active.Pointer);

        // Managers that live on inactive objects, plus ScriptableObject-based options.
        foreach (var candidate in Resources.FindObjectsOfTypeAll(type))
        {
            if (candidate != null)
                return new Il2CppSystem.Object(candidate.Pointer);
        }

        return null;
    }

    public static List<string> EnumNames(Il2CppSystem.Type type)
    {
        var names = new List<string>();
        try
        {
            foreach (var value in Il2CppSystem.Enum.GetValues(type))
                names.Add(value.ToString());
        }
        catch
        {
            // Nothing useful to say; the caller only uses this for a hint.
        }

        return names;
    }

    private static string ShortName(Il2CppSystem.Type type)
    {
        var full = type?.FullName ?? "?";
        var dot = full.LastIndexOf('.');
        return dot < 0 ? full : full.Substring(dot + 1);
    }
}
