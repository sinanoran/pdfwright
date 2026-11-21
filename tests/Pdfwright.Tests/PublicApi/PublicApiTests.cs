using System.Reflection;
using System.Text;

namespace Pdfwright.Tests.PublicApi;

/// <summary>
/// The public surface of the package, written out and compared with a file that is checked in. A change to what the package exposes (a type, a member,
/// a parameter, a base type) fails this test until <c>Pdfwright.PublicApi.txt</c> is changed in the same commit, so that a reviewer sees the surface
/// move. To regenerate it, run this test with <c>PDFWRIGHT_UPDATE_PUBLIC_API=1</c>.
/// </summary>
public class PublicApiTests
{
    private static string SnapshotPath => Path.Combine(TestPaths.RepositoryRoot, "tests", "Pdfwright.Tests", "PublicApi", "Pdfwright.PublicApi.txt");

    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void The_public_surface_is_the_one_in_the_checked_in_file()
    {
        string current = Dump(typeof(Length).Assembly);

        if (Environment.GetEnvironmentVariable("PDFWRIGHT_UPDATE_PUBLIC_API") == "1")
        {
            File.WriteAllText(SnapshotPath, current, new UTF8Encoding(false));
            return;
        }

        string approved = File.ReadAllText(SnapshotPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(
            approved == current,
            "The public API changed. Review the difference, then update tests/Pdfwright.Tests/PublicApi/Pdfwright.PublicApi.txt by running this test with PDFWRIGHT_UPDATE_PUBLIC_API=1.\n--- approved ---\n"
            + approved + "\n--- current ---\n" + current);
    }

    [Fact]
    public void Everything_public_is_in_the_root_namespace_and_nothing_else_leaks()
    {
        Type[] exported = [.. typeof(Length).Assembly.GetExportedTypes()];

        Assert.All(exported, t => Assert.Equal("Pdfwright", t.Namespace));
        Assert.DoesNotContain(exported, t => t.Name is "TrueTypeFont" or "ShapingFont" or "PdfDocumentWriter" or "ParagraphLayouter" or "Canvas");
    }

    internal static string Dump(Assembly assembly)
    {
        StringBuilder text = new();
        foreach (Type type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            text.Append(Kind(type)).Append(' ').Append(Name(type));
            List<string> bases = [];
            if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum))
            {
                bases.Add(Name(baseType));
            }

            bases.AddRange(type.GetInterfaces().Select(Name).Order(StringComparer.Ordinal));
            if (bases.Count > 0)
            {
                text.Append(" : ").Append(string.Join(", ", bases));
            }

            text.Append('\n');
            foreach (string member in Members(type).Order(StringComparer.Ordinal))
            {
                text.Append("  ").Append(member).Append('\n');
            }
        }

        return text.ToString();
    }

    private static string Kind(Type type) =>
        type.IsEnum ? "enum"
        : type.IsInterface ? "interface"
        : type.IsValueType ? "struct"
        : type.IsAbstract && type.IsSealed ? "static class"
        : type.IsAbstract ? "abstract class"
        : type.IsSealed ? "sealed class" : "class";

    private static IEnumerable<string> Members(Type type)
    {
        foreach (MemberInfo member in type.GetMembers(Declared))
        {
            if (member.Name.Contains('<', StringComparison.Ordinal))
            {
                continue; // compiler-generated (record clone, backing fields)
            }

            switch (member)
            {
                case FieldInfo f when f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly:
                    yield return type.IsEnum && f.IsStatic ? $"{f.Name} = {Convert.ToInt64(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)}" : $"field {Name(f.FieldType)} {f.Name}";
                    break;
                case ConstructorInfo c when c.IsPublic || c.IsFamily || c.IsFamilyOrAssembly:
                    yield return $"ctor({Parameters(c)})";
                    break;
                case PropertyInfo p:
                    MethodInfo? getter = p.GetMethod is { } g && Visible(g) ? g : null;
                    MethodInfo? setter = p.SetMethod is { } s && Visible(s) ? s : null;
                    if (getter is null && setter is null)
                    {
                        break;
                    }

                    string accessors = (getter is not null ? "get;" : string.Empty)
                        + (setter is null ? string.Empty : IsInit(setter) ? " init;" : " set;");
                    string stat = (getter ?? setter)!.IsStatic ? "static " : string.Empty;
                    yield return $"property {stat}{Name(p.PropertyType)} {p.Name} {{ {accessors.Trim()} }}";
                    break;
                case MethodInfo m when Visible(m) && !m.IsSpecialName:
                    yield return $"method {(m.IsStatic ? "static " : string.Empty)}{Name(m.ReturnType)} {m.Name}({Parameters(m)})";
                    break;
                case MethodInfo m when Visible(m) && m.Name.StartsWith("op_", StringComparison.Ordinal):
                    yield return $"operator {(m.IsStatic ? "static " : string.Empty)}{Name(m.ReturnType)} {m.Name}({Parameters(m)})";
                    break;
                case EventInfo e:
                    yield return $"event {Name(e.EventHandlerType!)} {e.Name}";
                    break;
            }
        }
    }

    private static bool Visible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsInit(MethodInfo setter) => setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    private static string Parameters(MethodBase method) =>
        string.Join(", ", method.GetParameters().Select(p => $"{Name(p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : string.Empty)}"));

    private static string Name(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsByRef)
        {
            return "ref " + Name(type.GetElementType()!);
        }

        if (type.IsArray)
        {
            return Name(type.GetElementType()!) + "[]";
        }

        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return Name(inner) + "?";
        }

        string name = type.Name;
        if (type.IsGenericType)
        {
            name = name[..name.IndexOf('`', StringComparison.Ordinal)] + "<" + string.Join(", ", type.GetGenericArguments().Select(Name)) + ">";
        }

        return type.Namespace is "System" or "System.Collections.Generic" or "System.Threading" or "System.Threading.Tasks" or "System.IO" or "Pdfwright" or null ? name : type.Namespace + "." + name;
    }
}
