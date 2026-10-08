using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>Combines successful extraction JSON files into one evidence-tagged index and skill library.</summary>
internal static class AnalysisComposer
{
    internal sealed record Symbol(string Name, string Kind, string Origin, string Source, string? Signature);
    internal static int Compose(string outputRoot)
    {
        var symbols = new List<Symbol>();
        var errors = new List<string>();
        void Read(string file, Action<JsonElement> action)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                action(doc.RootElement);
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
            { errors.Add(file + ": " + ex.Message); }
        }
        var managedDir = Path.Combine(outputRoot, "managed");
        if (Directory.Exists(managedDir))
            foreach (var file in Directory.EnumerateFiles(managedDir, "*.json", SearchOption.AllDirectories))
                Read(file, root =>
                {
                    foreach (var type in root.GetProperty("types").EnumerateArray())
                    {
                        var ns = type.GetProperty("namespace").GetString();
                        var name = type.GetProperty("name").GetString() ?? "";
                        var qualified = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                        symbols.Add(new Symbol(qualified, "type", "clr-metadata", file, null));
                        foreach (var method in type.GetProperty("methods").EnumerateArray())
                        {
                            var signature = method.TryGetProperty("decodedSignature", out var decoded)
                                && decoded.ValueKind == JsonValueKind.String ? decoded.GetString() : null;
                            symbols.Add(new Symbol(qualified + "." + method.GetProperty("name").GetString(),
                                "method", "clr-metadata", file, signature));
                        }
                    }
                });
        var nativeDir = Path.Combine(outputRoot, "native");
        if (Directory.Exists(nativeDir))
            foreach (var file in Directory.EnumerateFiles(nativeDir, "*.json", SearchOption.AllDirectories))
                Read(file, root =>
                {
                    foreach (var export in root.GetProperty("exports").EnumerateArray())
                    {
                        if (!export.TryGetProperty("name", out var name) ||
                            name.ValueKind != JsonValueKind.String) continue;
                        symbols.Add(new Symbol(name.GetString()!, "export", "pe-export-table", file, null));
                    }
                });
        var headersFile = Path.Combine(outputRoot, "sdk-headers.json");
        if (File.Exists(headersFile))
            Read(headersFile, root =>
            {
                foreach (var header in root.GetProperty("headers").EnumerateArray())
                {
                    if (!header.TryGetProperty("declarations", out var declarations)) continue;
                    var source = header.GetProperty("header").GetString() ?? headersFile;
                    foreach (var declaration in declarations.EnumerateArray())
                        symbols.Add(new Symbol(declaration.GetProperty("qualifiedName").GetString() ?? "",
                            declaration.GetProperty("kind").GetString() ?? "cpp-declaration",
                            "clang-ast", source,
                            declaration.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                                ? type.GetString() : null));
                }
            });
        var sorted = symbols.Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Distinct().OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Source, StringComparer.OrdinalIgnoreCase).ToArray();
        var indexDir = Path.Combine(outputRoot, "index");
        var skillsDir = Path.Combine(outputRoot, "skills");
        Directory.CreateDirectory(indexDir);
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(indexDir, "api-index.json"),
            JsonSerializer.Serialize(new { format = "unified-api-index-v1", symbols = sorted },
                new JsonSerializerOptions { WriteIndented = true }));
        var groups = sorted.GroupBy(x => x.Origin).OrderBy(x => x.Key).ToArray();
        var overview = new StringBuilder("---\nname: product-api\ndescription: Searchable managed and native API inventory with evidence provenance.\n---\n\n# Product API\n\n");
        overview.AppendLine("These records are extracted evidence, not proof of a supported callable API.");
        foreach (var group in groups)
        {
            var filename = group.Key + ".md";
            overview.AppendLine($"- [{group.Key}]({filename}) ({group.Count()} symbols)");
            using var writer = new StreamWriter(Path.Combine(skillsDir, filename));
            writer.WriteLine("# " + group.Key);
            writer.WriteLine();
            foreach (var symbol in group)
            {
                writer.WriteLine("## " + symbol.Name.Replace("\r", " ").Replace("\n", " "));
                writer.WriteLine();
                writer.WriteLine("- Kind: " + symbol.Kind);
                writer.WriteLine("- Source: `" + symbol.Source.Replace("`", "'") + "`");
                if (symbol.Signature is not null) writer.WriteLine("- Signature: `" + symbol.Signature.Replace("`", "'") + "`");
                writer.WriteLine();
            }
        }
        File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), overview.ToString());
        File.WriteAllText(Path.Combine(skillsDir, "INDEX.md"),
            "# Product API index\n\n" + string.Join("\n", groups.Select(g => $"- [{g.Key}]({g.Key}.md)")));
        File.WriteAllText(Path.Combine(outputRoot, "composition-report.json"),
            JsonSerializer.Serialize(new { symbols = sorted.Length, sources = groups.Select(g => new { origin = g.Key, count = g.Count() }), errors },
                new JsonSerializerOptions { WriteIndented = true }));
        DocumentationPublisher.Publish(outputRoot, sorted);
        return errors.Count;
    }
}
