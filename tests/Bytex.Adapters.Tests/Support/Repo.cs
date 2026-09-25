namespace Bytex.Adapters.Tests.Support;

/// <summary>
/// The repository the tests are running out of, for the guards that are kept honest by reading the source rather than
/// by reflecting over what was compiled. A test that lists what ships has to read it off disk: a list written down in
/// a test is a list that is right until somebody adds the next adapter and does not know the list exists.
/// </summary>
internal static class Repo
{
    /// <summary>The repository root, found by walking up to the solution file.</summary>
    public static string Root
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bytex.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                ?? throw new InvalidOperationException("no Bytex.slnx above " + AppContext.BaseDirectory);
        }
    }

    /// <summary>A path inside the repository.</summary>
    public static string Path_(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>The adapters that ship, read off disk so that a new one is noticed the day it lands.</summary>
    public static string[] ShippedVenues() =>
        Directory.EnumerateDirectories(Path.Combine(Root, "src"), "Bytex.Adapters.*")
            .Select(d => Path.GetFileName(d)["Bytex.Adapters.".Length..])
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Every C# file of one adapter, excluding build output.</summary>
    public static IEnumerable<string> SourceFiles(string venue) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src", "Bytex.Adapters." + venue), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
}
