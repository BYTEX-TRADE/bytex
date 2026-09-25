namespace Bytex.Cli.Tests.Support;

/// <summary>
/// The repository the tests were built from, found by walking up from the test assembly until the solution file
/// appears. The tests that check what the repository ships - the image, the example configurations, the notebook,
/// the package settings - read the real files rather than copies that could drift from them.
/// </summary>
internal static class RepoRoot
{
    private static readonly Lazy<string> _path = new(Find);

    public static string Path => _path.Value;

    public static string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public static string Read(params string[] parts) => File.ReadAllText(Combine(parts));

    private static string Find()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Bytex.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
