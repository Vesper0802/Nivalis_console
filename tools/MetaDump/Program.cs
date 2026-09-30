using Mono.Cecil;

// Dumps signatures of selected types out of the BepInEx-generated IL2CPP interop
// assemblies. Interop assemblies have no method bodies, so this only tells us the
// shape of the API we can patch against, not the game logic.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: MetaDump <assembly.dll> <type-name-substring> [more...]");
    return 1;
}

var asmPath = args[0];
var needles = args.Skip(1).ToArray();

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(asmPath))!);
using var asm = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { AssemblyResolver = resolver });

var types = asm.MainModule.GetTypes()
    .Where(t => needles.Any(n => t.FullName.Contains(n, StringComparison.OrdinalIgnoreCase)))
    .OrderBy(t => t.FullName)
    .ToList();

Console.WriteLine($"// {asm.Name.Name}: {types.Count} matching types");

foreach (var t in types)
{
    Console.WriteLine();
    Console.WriteLine($"=== {t.FullName}  (base: {t.BaseType?.FullName}) ===");

    foreach (var f in t.Fields)
    {
        // Interop types store an offset/pointer cache per member; those are noise.
        if (f.Name.StartsWith("NativeFieldInfoPtr_") || f.Name.StartsWith("NativeMethodInfoPtr_"))
            continue;
        var mods = (f.IsStatic ? "static " : "") + (f.IsPublic ? "public " : "private ");
        Console.WriteLine($"  field  {mods}{Short(f.FieldType)} {f.Name}");
    }

    foreach (var p in t.Properties)
        Console.WriteLine($"  prop   {Short(p.PropertyType)} {p.Name} {{ {(p.GetMethod != null ? "get; " : "")}{(p.SetMethod != null ? "set; " : "")}}}");

    foreach (var m in t.Methods)
    {
        if (m.IsConstructor && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Name == "IntPtr")
            continue;
        var mods = m.IsStatic ? "static " : "";
        var ps = string.Join(", ", m.Parameters.Select(p => $"{Short(p.ParameterType)} {p.Name}"));
        Console.WriteLine($"  method {mods}{Short(m.ReturnType)} {m.Name}({ps})");
    }
}

return 0;

// Only trim well-known leading namespaces. Replacing them anywhere in the string
// corrupts game type names such as Nivalis.InventorySystem.ItemType.
static string Short(TypeReference t)
{
    var name = t.FullName;
    foreach (var prefix in new[] { "Il2CppSystem.", "System.", "UnityEngine." })
    {
        if (name.StartsWith(prefix, StringComparison.Ordinal))
            return name.Substring(prefix.Length);
    }
    return name;
}
