using System.Diagnostics;
using System.Text;

namespace Bytex.Cli.Tests.Support;

internal sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public string AllOutput => StdOut + "\n" + StdErr;
}

/// <summary>
/// Runs the real <c>bytex</c> executable (the assembly copied next to the tests) as a child process, the way a
/// user would. The CLI's types are internal, so its command line is its public API.
/// </summary>
internal static class CliRunner
{
    /// <summary>Venue credential variables are never inherited by the child, so keys on the developer's machine cannot leak into a test.</summary>
    private static readonly string[] _scrubbed =
    [
        "BINANCE_API_KEY", "BINANCE_API_SECRET", "BINANCE_TESTNET_API_KEY", "BINANCE_TESTNET_API_SECRET",
        "BYBIT_API_KEY", "BYBIT_API_SECRET", "BYBIT_TESTNET_API_KEY", "BYBIT_TESTNET_API_SECRET", "TARDIS_API_KEY",
    ];

    public static async Task<CliResult> RunAsync(IEnumerable<string> args, string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null, TimeSpan? timeout = null)
    {
        string cli = Path.Combine(AppContext.BaseDirectory, "bytex.dll");
        Assert.True(File.Exists(cli), $"CLI assembly not found next to the tests: {cli}");

        ProcessStartInfo info = new(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.ArgumentList.Add(cli);
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        foreach (string variable in _scrubbed)
        {
            info.Environment.Remove(variable);
        }

        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                info.Environment[key] = value;
            }
        }

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("could not start the CLI process");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource cts = new(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"bytex {string.Join(' ', args)} did not exit in time.\nstdout:\n{await stdout}\nstderr:\n{await stderr}");
        }

        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    private static string DotnetHost()
    {
        string? fromSdk = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromSdk) && File.Exists(fromSdk))
        {
            return fromSdk;
        }

        string? current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        return "dotnet";
    }
}

/// <summary>
/// A scratch directory under the system temp path, removed when the test ends.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bytex-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name, string content)
    {
        string path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A lingering handle on Windows must not fail the test that already passed or failed on its own merits.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
