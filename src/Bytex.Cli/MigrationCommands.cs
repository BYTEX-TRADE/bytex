using System.CommandLine;
using Bytex.Data;
using Bytex.Documents.Schema;

namespace Bytex.Cli;

internal static class MigrationCommands
{
    internal static Command Build()
    {
        Command root = new("migrate", "Explicit conversion of legacy BYTEX data into separate destinations");
        Option<string> source = new("--source") { Required = true };
        Option<string> destination = new("--destination") { Required = true };
        Command archive = new("archive", "Convert a legacy archive; resume only with an unchanged source");
        archive.Options.Add(source);
        archive.Options.Add(destination);
        archive.SetAction(async (result, ct) =>
        {
            int count = await LegacyArchiveMigration.ConvertAsync(ObjectStores.Open(result.GetValue(source)!), ObjectStores.Open(result.GetValue(destination)!), ct).ConfigureAwait(false);
            Console.WriteLine($"Verified {count} source objects. Source preserved; destination checkpoint complete.");
            return 0;
        });
        Command document = new("document", "Convert a v1 strategy document without overwriting the original");
        document.Options.Add(source);
        document.Options.Add(destination);
        document.SetAction(async (result, ct) =>
        {
            string from = Path.GetFullPath(result.GetValue(source)!);
            string to = Path.GetFullPath(result.GetValue(destination)!);
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Source and destination must differ."); }
            string converted = LegacyDocumentMigration.Convert(await File.ReadAllTextAsync(from, ct).ConfigureAwait(false));
            if (File.Exists(to))
            {
                if (await File.ReadAllTextAsync(to, ct).ConfigureAwait(false) != converted) { throw new IOException("Existing destination differs; refusing overwrite."); }
            }
            else
            {
                LocalObjectStore store = new(Path.GetDirectoryName(to)!);
                await store.WriteAsync(Path.GetFileName(to), stream => stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(converted), ct).AsTask(), ct).ConfigureAwait(false);
            }
            Console.WriteLine("Document converted; original preserved.");
            return 0;
        });
        root.Subcommands.Add(archive);
        root.Subcommands.Add(document);
        return root;
    }
}
