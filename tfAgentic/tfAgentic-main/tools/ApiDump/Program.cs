using System.Reflection;
using System.Text;

var baseDir = AppContext.BaseDirectory;
var patterns = new[] { "Microsoft.Agents.AI", "ModelContextProtocol", "A2A", "Microsoft.Extensions.AI" };
var dlls = Directory.GetFiles(baseDir, "*.dll")
    .Where(f => patterns.Any(p => Path.GetFileName(f).StartsWith(p, StringComparison.OrdinalIgnoreCase)))
    .OrderBy(f => f)
    .ToList();

var asms = new List<Assembly>();
foreach (var dll in dlls)
{
    try { asms.Add(Assembly.LoadFrom(dll)); }
    catch (Exception ex) { Console.Error.WriteLine($"load fail {Path.GetFileName(dll)}: {ex.Message}"); }
}

static string TypeName(Type t)
{
    if (t.IsByRef) return "ref " + TypeName(t.GetElementType()!);
    if (t.IsGenericType)
    {
        var name = t.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        var args = string.Join(", ", t.GetGenericArguments().Select(TypeName));
        return $"{name}<{args}>";
    }
    return t.Name;
}

static string MethodSig(MethodInfo m)
{
    var ps = string.Join(", ", m.GetParameters().Select(p =>
    {
        var def = p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : "";
        var attr = p.GetCustomAttributes().Any(a => a.GetType().Name == "ParamArrayAttribute") ? "params " : "";
        return $"{attr}{TypeName(p.ParameterType)} {p.Name}{def}";
    }));
    var gen = m.IsGenericMethodDefinition ? "<" + string.Join(",", m.GetGenericArguments().Select(g => g.Name)) + ">" : "";
    var mods = (m.IsStatic ? "static " : "");
    var ext = m.GetParameters().FirstOrDefault()?.GetCustomAttributes().Any(a => a.GetType().Name == "ExtensionAttribute") == true ? "[ext] " : "";
    return $"  {ext}{mods}{TypeName(m.ReturnType)} {m.Name}{gen}({ps})";
}

var sb = new StringBuilder();
foreach (var asm in asms)
{
    sb.AppendLine($"\n========== ASSEMBLY: {asm.GetName().Name} {asm.GetName().Version} ==========");
    Type[] types;
    try { types = asm.GetExportedTypes(); }
    catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray(); }
    catch (Exception ex) { sb.AppendLine($"// types fail: {ex.Message}"); continue; }

    foreach (var t in types.OrderBy(t => t.Namespace).ThenBy(t => t.Name))
    {
        var kind = t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsInterface ? "interface" : t.IsAbstract && t.IsSealed ? "static class" : t.IsAbstract ? "abstract class" : "class";
        sb.AppendLine($"\n[{kind}] {t.FullName}");
        if (t.IsEnum)
        {
            sb.AppendLine("  values: " + string.Join(", ", Enum.GetNames(t)));
            continue;
        }
        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var ps = string.Join(", ", c.GetParameters().Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));
            sb.AppendLine($"  .ctor({ps})");
        }
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                           .Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
            sb.AppendLine(MethodSig(m));
        foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            sb.AppendLine($"  prop {TypeName(pr.PropertyType)} {pr.Name} {{ {(pr.CanRead ? "get; " : "")}{(pr.CanWrite ? "set; " : "")}}}");
    }
}

var outPath = Path.Combine(args.Length > 0 ? args[0] : ".", "maf_api.txt");
File.WriteAllText(outPath, sb.ToString());
Console.WriteLine($"Wrote {outPath} ({sb.Length} chars, {asms.Count} assemblies)");
