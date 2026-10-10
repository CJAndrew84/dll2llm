using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DllToLLMDoc;

// Builds Git-readable navigation from existing product metadata without loading vendor DLLs.
// Deliberately does not infer signatures or semantics from native export names.
internal static class ProductCatalog
{
    private sealed record Entry(string Name, string Kind, string Source, string Evidence);
    public static int Run(string[] args)
    {
        string? source = null, output = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--source" && i + 1 < args.Length) source = args[++i];
            else if (args[i] == "--output" && i + 1 < args.Length) output = args[++i];
            else { Console.Error.WriteLine("Usage: dll2llm catalog --source <product-analysis-folder> --output <folder>"); return 2; }
        }
        if (source is null || output is null || !Directory.Exists(source)) return 2;
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        if (output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Output must be outside source to avoid recursive indexing.");
            return 2;
        }
        var entries = new List<Entry>();
        int scanned = 0, invalid = 0;
        var symbols = new List<SymbolCatalog.Symbol>();
        foreach (var file in Directory.EnumerateFiles(source, "*.json", SearchOption.AllDirectories)
                     .Where(p => p.Contains(Path.DirectorySeparatorChar + "managed" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                              || p.Contains(Path.DirectorySeparatorChar + "native" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            scanned++;
            string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { invalid++; continue; }
                var kind = relative.StartsWith("native/", StringComparison.OrdinalIgnoreCase) ? "native-symbol" : "managed-metadata";
                // Use source filename as a guaranteed provenance record. Never fabricate types or methods.
                entries.Add(new Entry(Path.GetFileNameWithoutExtension(file), kind, relative, "metadata-file"));
                symbols.AddRange(SymbolCatalog.Extract(doc.RootElement, relative, kind == "native-symbol"));
            }
            catch (JsonException) { invalid++; }
            catch (IOException) { invalid++; }
        }
        Directory.CreateDirectory(output);
        symbols = symbols.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        var opts = new JsonSerializerOptions { WriteIndented = false };
        var ndjson = entries.Select(e => JsonSerializer.Serialize(e, opts));
        WriteIfChanged(Path.Combine(output, "catalog.jsonl"), string.Join("\n", ndjson) + (entries.Count > 0 ? "\n" : ""));
        WriteIfChanged(Path.Combine(output, "symbols.jsonl"), string.Join("\\n", symbols.Select(x => JsonSerializer.Serialize(x))) + (symbols.Count > 0 ? "\\n" : ""));
        var sb = new StringBuilder("# Product API navigation\n\n");
        sb.AppendLine("Generated from extracted metadata. Entries are source-file records, **not verified callable APIs**.");
        sb.AppendLine("Native exports do not establish C++ method signatures. Consult SDK headers and PDBs.");
        sb.AppendLine().AppendLine($"Scanned: {scanned}; indexed files: {entries.Count}; extracted symbols: {symbols.Count}; invalid/unreadable: {invalid}.").AppendLine();
        sb.AppendLine("## Source files").AppendLine();
        foreach (var group in entries.GroupBy(e => e.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"### {group.Key}").AppendLine();
            foreach (var entry in group)
                sb.AppendLine($"- `{Escape(entry.Name)}` — `{Escape(entry.Source)}`");
            sb.AppendLine();
        }
        sb.AppendLine("## Extracted symbols").AppendLine();
        foreach (var symbol in symbols)
            sb.AppendLine($"- `{Escape(symbol.Kind)}` `{Escape(symbol.Name)}` — `{Escape(symbol.Source)}` ({symbol.Evidence})");
        // Split large human-readable navigation into bounded chunks, keep entrypoint small.
        var lines = sb.ToString().Split('\n');
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var line in lines)
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > 48_000)
            {
                chunks.Add(current.ToString()); current.Clear();
            }
            current.AppendLine(line);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        var index = new StringBuilder("# API catalogue\n\nThis is an inventory of metadata files, not a validated API reference.\n\n");
        for (int i = 0; i < chunks.Count; i++)
        {
            string name = $"navigation-{i + 1:D3}.md";
            WriteIfChanged(Path.Combine(output, name), chunks[i]);
            index.AppendLine($"- [Part {i + 1}]({name})");
        }
        WriteIfChanged(Path.Combine(output, "README.md"), index.ToString());
        WriteIfChanged(Path.Combine(output, "SKILL.md"), "---\nname: product-api-catalog\ndescription: Navigate extracted managed and native API metadata with provenance\n---\n\nRead [README.md](README.md) first. Consult source JSON for exact symbols; never infer signatures from native exports.\n");
        Console.WriteLine($"Indexed {entries.Count} metadata files; {invalid} invalid/unreadable.");
        return invalid == 0 ? 0 : 1;
    }
    private static string Escape(string s) => s.Replace("`", "&#96;").Replace("\r", " ").Replace("\n", " ");
    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
