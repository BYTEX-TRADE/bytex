namespace Bytex.Cli;

/// <summary>
/// Loads KEY=VALUE lines from a file into this process's environment. Used so that a node process reads its own
/// venue credentials from a file the launching host never opens.
/// </summary>
internal static class EnvFile
{
    public static int Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Environment file not found: {path}", path);
        }

        int count = 0;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            Environment.SetEnvironmentVariable(key, value);
            count++;
        }

        return count;
    }
}
