using System.Text;

namespace Bytex.Data.Tests.Support;

/// <summary>
/// A unique scratch directory under the system temp path, removed on dispose.
/// xunit creates one test-class instance per test, so every test gets its own directory.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "bytex-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Writes the lines joined with '\n' only, so that the content is identical on every platform.</summary>
    public string WriteLines(string fileName, params string[] lines) => WriteText(fileName, string.Join('\n', lines) + "\n");

    public string WriteText(string fileName, string content)
    {
        string path = Path.Combine(Root, fileName);
        File.WriteAllText(path, content, Utf8NoBom);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone; nothing to clean up.
        }
    }
}
