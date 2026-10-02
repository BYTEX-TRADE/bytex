using System.Globalization;
using System.Reflection;
using System.Text;
using Bytex.Cli.Tests.Support;

namespace Bytex.Cli.Tests;

// Why: an API freeze that is a sentence in a readme is a promise nobody can keep. This is the same promise as a guard: the
// public surface of every packable library is written down under api/, and a change to it fails here until the file is
// updated in the same commit.
//
// Before 1.0 that is a prompt rather than a refusal - a minor version may change an API, and the diff in the failure
// message is the list of what would change, which is exactly what a changelog entry needs. From 1.0 the same guard is the
// freeze: a member that disappears or changes shape cannot reach a release without somebody deciding it is a major.
//
// It is reflection over the built assemblies rather than a parse of the source, because what ships is the assembly: a
// member made public by a generator, a record's synthesised surface, an interface implemented as a side effect are all
// real API and none of them are obvious in the source.
public class PublicSurfaceTests
{
    /// <summary>
    /// The libraries whose surface is a promise. The CLI is a tool rather than a library - nobody compiles against it -
    /// so its own types are not part of this.
    /// </summary>
    private static readonly string[] _libraries =
    [
        "Bytex.Core", "Bytex.Data", "Bytex.Indicators", "Bytex.Documents", "Bytex.Backtest", "Bytex.Live",
        "Bytex.Adapters.Binance", "Bytex.Adapters.Bitget", "Bytex.Adapters.Bybit", "Bytex.Adapters.Databento",
        "Bytex.Adapters.Gate", "Bytex.Adapters.Hyperliquid", "Bytex.Adapters.Kraken", "Bytex.Adapters.Kucoin",
        "Bytex.Adapters.Okx", "Bytex.Adapters.Tardis", "Bytex.Persistence.Postgres", "Bytex.Persistence.Redis",
        "Bytex.Persistence.S3",
    ];

    public static TheoryData<string> Libraries() => new(_libraries);

    [Theory]
    [InlineData(nameof(AccessorFixture.InitOnly), "init;")]
    [InlineData(nameof(AccessorFixture.Mutable), "set;")]
    public void Property_descriptions_distinguish_init_from_set(string name, string accessor)
    {
        PropertyInfo property = typeof(AccessorFixture).GetProperty(name)!;
        Assert.Equal($"{name} : System.String {{ get; {accessor} }}", Describe(property));
    }

    private sealed class AccessorFixture
    {
        public string InitOnly { get; init; } = string.Empty;

        public string Mutable { get; set; } = string.Empty;
    }

    [Theory]
    [MemberData(nameof(Libraries))]
    public void The_public_surface_is_the_one_that_was_written_down(string library)
    {
        string expectedPath = RepoRoot.Combine("api", library + ".txt");
        string actual = Surface(Load(library));

        if (!File.Exists(expectedPath))
        {
            File.WriteAllText(expectedPath, actual);
            Assert.Fail($"There was no surface written down for {library}; one has been written to {expectedPath}. Read it and commit it with the change that needed it.");
        }

        string expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        // The failure names what changed rather than only that something did: the first lines of the difference are what
        // a changelog entry has to describe, and a surface file is far too long to compare by eye.
        string[] before = expected.Split('\n');
        string[] after = actual.Split('\n');
        List<string> removed = [.. before.Except(after, StringComparer.Ordinal).Take(10)];
        List<string> added = [.. after.Except(before, StringComparer.Ordinal).Take(10)];
        string written = Path.Combine(Path.GetTempPath(), $"{library}.surface.txt");
        File.WriteAllText(written, actual);

        Assert.Fail(
            $"The public surface of {library} is not the one in api/{library}.txt."
            + (removed.Count > 0 ? $"{Environment.NewLine}Gone (up to ten):{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", removed)}" : string.Empty)
            + (added.Count > 0 ? $"{Environment.NewLine}New (up to ten):{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", added)}" : string.Empty)
            + $"{Environment.NewLine}If the change is meant, replace api/{library}.txt with {written} in the same commit."
            + " Something GONE from a released surface is a breaking change; see docs/versioning.md.");
    }

    [Fact]
    public void Every_packable_library_has_its_surface_written_down()
    {
        // A library added to src/ without a surface file would otherwise be frozen by nothing at all.
        //
        // A library is a directory holding a PROJECT. Not every directory under src/ is one: the key layout shared
        // by the backing stores lives there as source, compiled into each of them, and produces no assembly of its
        // own - so there is no surface to write down and nothing for this to freeze. Listing it as a library would
        // have demanded an api file for an assembly that does not exist; ignoring directories without a project
        // keeps the teeth where they belong, on anything that really ships.
        IEnumerable<string> packable = Directory.EnumerateDirectories(RepoRoot.Combine("src"))
            .Where(directory => Directory.EnumerateFiles(directory, "*.csproj").Any())
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !string.Equals(name, "Bytex.Cli", StringComparison.Ordinal));

        Assert.Equal(_libraries.Order(StringComparer.Ordinal), packable.Order(StringComparer.Ordinal));
        foreach (string library in _libraries)
        {
            Assert.True(File.Exists(RepoRoot.Combine("api", library + ".txt")), $"api/{library}.txt is missing");
        }
    }

    private static Assembly Load(string library) =>
        Assembly.Load(new AssemblyName(library));

    /// <summary>
    /// Every public and protected member of every exported type, one to a line, in an order that does not depend on
    /// reflection's own.
    ///
    /// <para>
    /// What is left out: anything a compiler invented for a record or a closure, which has a name no source can use, and
    /// property accessors, which travel with the property they belong to. Everything else is in, including operators and
    /// the members a record synthesises, because they are all things somebody can compile against.
    /// </para>
    /// </summary>
    private static string Surface(Assembly assembly)
    {
        List<string> lines = new();
        foreach (Type type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            string kind = type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : "class";
            StringBuilder header = new($"{kind} {Name(type)}");
            if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType) && baseType != typeof(Enum))
            {
                header.Append(" : ").Append(Name(baseType));
            }

            string[] interfaces = [.. type.GetInterfaces().Where(i => i.IsPublic).Select(Name).Order(StringComparer.Ordinal)];
            if (interfaces.Length > 0)
            {
                header.Append(type.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum) ? ", " : " : ").Append(string.Join(", ", interfaces));
            }

            lines.Add(header.ToString());

            List<string> members = [];
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (Describe(member) is { } description)
                {
                    members.Add("    " + description);
                }
            }

            // Sorted within the type, so a type keeps its own members: a file sorted line by line interleaves the members
            // of every type in the assembly, and a diff of it says nothing about where the change was.
            members.Sort(StringComparer.Ordinal);
            lines.AddRange(members);
        }

        return string.Join('\n', lines) + '\n';
    }

    private static string? Describe(MemberInfo member)
    {
        if (member.Name.Contains('<', StringComparison.Ordinal) || member.Name.Contains('>', StringComparison.Ordinal))
        {
            return null;
        }

        switch (member)
        {
            case ConstructorInfo constructor when Visible(constructor.IsPublic, constructor.IsFamily, constructor.IsFamilyOrAssembly):
                return $".ctor({Parameters(constructor)})";

            case MethodInfo method when Visible(method.IsPublic, method.IsFamily, method.IsFamilyOrAssembly) && !method.IsSpecialName:
                return $"{method.Name}{Generics(method)}({Parameters(method)}) -> {Name(method.ReturnType)}";

            // An operator is special-named and is still API: somebody writes a + b against it.
            case MethodInfo op when Visible(op.IsPublic, op.IsFamily, op.IsFamilyOrAssembly) && op.Name.StartsWith("op_", StringComparison.Ordinal):
                return $"{op.Name}({Parameters(op)}) -> {Name(op.ReturnType)}";

            case PropertyInfo property when Accessible(property):
                return $"{property.Name} : {Name(property.PropertyType)} {{ {(property.GetMethod is { } g && Visible(g.IsPublic, g.IsFamily, g.IsFamilyOrAssembly) ? "get; " : string.Empty)}{(property.SetMethod is { } s && Visible(s.IsPublic, s.IsFamily, s.IsFamilyOrAssembly) ? (s.ReturnParameter.GetRequiredCustomModifiers().Any(m => m == typeof(System.Runtime.CompilerServices.IsExternalInit)) ? "init; " : "set; ") : string.Empty)}}}";

            case FieldInfo field when Visible(field.IsPublic, field.IsFamily, field.IsFamilyOrAssembly):
                return field.IsLiteral
                    ? $"{field.Name} = {Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture)} : {Name(field.FieldType)}"
                    : $"{field.Name} : {Name(field.FieldType)}{(field.IsStatic ? " static" : string.Empty)}";

            case EventInfo declared when declared.AddMethod is { } add && Visible(add.IsPublic, add.IsFamily, add.IsFamilyOrAssembly):
                return $"event {declared.Name} : {Name(declared.EventHandlerType!)}";

            default:
                return null;
        }
    }

    private static bool Visible(bool isPublic, bool isFamily, bool isFamilyOrAssembly) => isPublic || isFamily || isFamilyOrAssembly;

    private static bool Accessible(PropertyInfo property) =>
        (property.GetMethod is { } g && Visible(g.IsPublic, g.IsFamily, g.IsFamilyOrAssembly))
        || (property.SetMethod is { } s && Visible(s.IsPublic, s.IsFamily, s.IsFamilyOrAssembly));

    private static string Parameters(MethodBase method) =>
        string.Join(", ", method.GetParameters().Select(p => $"{Name(p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = " + (p.RawDefaultValue?.ToString() ?? "null") : string.Empty)}"));

    private static string Generics(MethodInfo method) =>
        method.IsGenericMethodDefinition ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">" : string.Empty;

    /// <summary>A type as somebody writing against it would say it, with generic arguments and without the assembly.</summary>
    private static string Name(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray)
        {
            return Name(type.GetElementType()!) + "[]";
        }

        if (type.IsByRef)
        {
            return Name(type.GetElementType()!) + "&";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        string bare = (type.GetGenericTypeDefinition().FullName ?? type.Name).Split('`')[0];
        return $"{bare}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>";
    }
}
