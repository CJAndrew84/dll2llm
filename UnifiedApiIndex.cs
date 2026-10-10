using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>Creates a unified, provenance-aware searchable symbol catalogue.</summary>
internal static class UnifiedApiIndex
{
    internal sealed record Entry(string Name, string Kind, string Origin, string Source, string? Signature);

    internal static void Build(string managedFile, string headersFile, string exportsFile, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var entries = new List<Entry>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(managedFile)))
        {
            foreach (var type in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                var ns = type.GetProperty("namespace").GetString();
                var name = type.GetProperty("name").GetString() ?? "";
                var qualified = string.IsNullOrWhiteSpace(ns) ? name : ns + "." + name;
                entries.Add(new Entry(qualified, "type", "clr-metadata", managedFile, null));
                foreach (var method in type.GetProperty("methods").EnumerateArray())
                {
                    var signature = method.TryGetProperty("decodedSignature", out var decoded)
                        && decoded.ValueKind == JsonValueKind.String ? decoded.GetString() : null;
                    entries.Add(new Entry(qualified + "." + method.GetProperty("name").GetString(),
                        "method", "clr-metadata", managedFile, signature));
                }
            }
        }
        using (var doc = JsonDocument.Parse(File.ReadAllText(headersFile)))
        {
            foreach (var header in doc.RootElement.GetProperty("headers").EnumerateArray())
            {
                if (!header.TryGetProperty("declarations", out var declarations)) continue;
                var source = header.GetProperty("header").GetString() ?? headersFile;
                foreach (var declaration in declarations.EnumerateArray())
                    entries.Add(new Entry(declaration.GetProperty("qualifiedName").GetString() ?? "",
                        declaration.GetProperty("kind").GetString() ?? "cpp-declaration",
                        "clang-ast", source,
                        declaration.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                            ? type.GetString() : null));
            }
        }
        using (var doc = JsonDocument.Parse(File.ReadAllText(exportsFile)))
        {
            foreach (var export in doc.RootElement.GetProperty("exports").EnumerateArray())
            {
                if (!export.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                entries.Add(new Entry(name.GetString()!, "export", "pe-export-table", exportsFile, null));
            }
        }
        var sorted = entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Origin).ThenBy(x => x.Source).ToArray();
        File.WriteAllText(Path.Combine(outputDir, "api-index.json"),
            JsonSerializer.Serialize(new { format = "unified-api-index-v1", symbols = sorted },
                new JsonSerializerOptions { WriteIndented = true }));
        using var writer = new StreamWriter(Path.Combine(outputDir, "INDEX.md"));
        writer.WriteLine("# Unified API Symbol Index");
        writer.WriteLine();
        writer.WriteLine($"Indexed symbols: {sorted.Length}");
        writer.WriteLine();
        writer.WriteLine("| Name | Kind | Evidence |");
        writer.WriteLine("|---|---|---|");
        foreach (var entry in sorted)
            writer.WriteLine($"| {Escape(entry.Name)} | {Escape(entry.Kind)} | {Escape(entry.Origin)} |");
    }

    internal static void Search(string file, string query, int limit)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var results = doc.RootElement.GetProperty("symbols").EnumerateArray()
            .Where(x => (x.GetProperty("name").GetString() ?? "")
                .Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit);
        foreach (var item in results)
            Console.WriteLine($"{item.GetProperty("name").GetString()} [{item.GetProperty("origin").GetString()}] " +
                              $"{(item.TryGetProperty("signature", out var sig) ? sig.ToString() : "")}");
    }

    private static string Escape(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
