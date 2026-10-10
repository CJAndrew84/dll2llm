using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>Combines successful extraction JSON files into one evidence-tagged index and skill library.</summary>
internal static class AnalysisComposer
{
    internal sealed record Symbol(string Name, string Kind, string Origin, string Source, string? Signature);

    private const int IndexShardCount = 32;
    private const int SkillShardCount = 32;

    internal static int Compose(string outputRoot)
    {
        var errors = new List<string>();
        var sourceCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        long symbolCount = 0;

        var tempDir = Path.Combine(outputRoot, ".compose-temp");
        Directory.CreateDirectory(tempDir);
        var symbolStream = Path.Combine(tempDir, "symbols.ndjson");

        void Read(string file, Action<JsonElement> action)
        {
            try
            {
                using var input = File.OpenRead(file);
                using var doc = JsonDocument.Parse(input);
                action(doc.RootElement);
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
            { errors.Add(file + ": " + ex.Message); }
        }

        try
        {
            using (var streamWriter = new StreamWriter(symbolStream, false, Encoding.UTF8))
            {
                void Emit(Symbol symbol)
                {
                    if (string.IsNullOrWhiteSpace(symbol.Name)) return;
                    streamWriter.WriteLine(JsonSerializer.Serialize(symbol));
                    symbolCount++;
                    sourceCounts[symbol.Origin] = sourceCounts.TryGetValue(symbol.Origin, out var count) ? count + 1 : 1;
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
                                Emit(new Symbol(qualified, "type", "clr-metadata", file, null));
                                foreach (var method in type.GetProperty("methods").EnumerateArray())
                                {
                                    var signature = method.TryGetProperty("decodedSignature", out var decoded)
                                        && decoded.ValueKind == JsonValueKind.String ? decoded.GetString() : null;
                                    Emit(new Symbol(qualified + "." + method.GetProperty("name").GetString(),
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
                                Emit(new Symbol(name.GetString()!, "export", "pe-export-table", file, null));
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
                                Emit(new Symbol(declaration.GetProperty("qualifiedName").GetString() ?? "",
                                    declaration.GetProperty("kind").GetString() ?? "cpp-declaration",
                                    "clang-ast", source,
                                    declaration.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                                        ? type.GetString() : null));
                        }
                    });
            }

            var indexDir = Path.Combine(outputRoot, "index");
            var skillsDir = Path.Combine(outputRoot, "skills");
            Directory.CreateDirectory(indexDir);
            Directory.CreateDirectory(skillsDir);

            WriteIndex(Path.Combine(indexDir, "api-index.json"), symbolStream);
            WriteSkills(skillsDir, symbolStream, sourceCounts);

            var groups = sourceCounts.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
            var overview = new StringBuilder("---\nname: product-api\ndescription: Searchable managed and native API inventory with evidence provenance.\n---\n\n# Product API\n\n");
            overview.AppendLine("These records are extracted evidence, not proof of a supported callable API.");

            foreach (var group in groups)
                overview.AppendLine($"- [{group.Key}]({group.Key}.md) ({group.Value} symbols)");

            File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), overview.ToString());
            File.WriteAllText(Path.Combine(skillsDir, "INDEX.md"),
                "# Product API index\n\n" + string.Join("\n", groups.Select(g => $"- [{g.Key}]({g.Key}.md)")));

            File.WriteAllText(Path.Combine(outputRoot, "composition-report.json"),
                JsonSerializer.Serialize(new { symbols = symbolCount, sources = groups.Select(g => new { origin = g.Key, count = g.Value }), errors },
                    new JsonSerializerOptions { WriteIndented = true }));

            DocumentationPublisher.Publish(outputRoot, symbolStream);
            return errors.Count;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); }
            catch { }
        }
    }

    private static void WriteIndex(string destination, string symbolStream)
    {
        var indexDir = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Missing index directory.");
        Directory.CreateDirectory(indexDir);

        var shardWriters = new StreamWriter[IndexShardCount];
        var shardFiles = new string[IndexShardCount];
        var shardCounts = new long[IndexShardCount];

        try
        {
            for (var i = 0; i < IndexShardCount; i++)
            {
                shardFiles[i] = $"api-index-{i + 1:D2}.ndjson";
                shardWriters[i] = new StreamWriter(Path.Combine(indexDir, shardFiles[i]), false, Encoding.UTF8);
            }

            foreach (var symbol in ReadSymbols(symbolStream))
            {
                var shard = SelectShard(symbol.Name, IndexShardCount);
                shardCounts[shard]++;
                shardWriters[shard].WriteLine(JsonSerializer.Serialize(new
                {
                    name = symbol.Name,
                    kind = symbol.Kind,
                    origin = symbol.Origin
                }));
            }
        }
        finally
        {
            foreach (var writer in shardWriters)
                writer?.Dispose();
        }

        File.WriteAllText(destination,
            JsonSerializer.Serialize(new
            {
                format = "unified-api-index-v2",
                shardFormat = "ndjson",
                shardCount = IndexShardCount,
                shards = shardFiles,
                counts = shardCounts
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSkills(string skillsDir, string symbolStream, IReadOnlyDictionary<string, long> sourceCounts)
    {
        var manifestCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        var manifestShards = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var writers = new Dictionary<string, StreamWriter>(StringComparer.Ordinal);
        try
        {
            foreach (var symbol in ReadSymbols(symbolStream))
            {
                var shardIndex = SelectShard(symbol.Name, SkillShardCount);
                var shardKey = symbol.Origin + "-" + (shardIndex + 1).ToString("D2");

                if (!writers.TryGetValue(shardKey, out var writer))
                {
                    writer = new StreamWriter(Path.Combine(skillsDir, shardKey + ".md"), false, Encoding.UTF8);
                    writer.WriteLine("# " + symbol.Origin + " shard " + (shardIndex + 1).ToString("D2"));
                    writer.WriteLine();
                    writer.WriteLine("Compact symbol summary. Use the API index for exhaustive machine-readable lookup.");
                    writer.WriteLine();
                    writers[shardKey] = writer;

                    if (!manifestShards.TryGetValue(symbol.Origin, out var shardList))
                    {
                        shardList = new List<string>();
                        manifestShards[symbol.Origin] = shardList;
                    }

                    shardList.Add(shardKey + ".md");
                }

                writer.WriteLine("## " + Clean(symbol.Name));
                writer.WriteLine();
                writer.WriteLine("- Kind: " + Clean(symbol.Kind));
                writer.WriteLine();

                manifestCounts[symbol.Origin] = manifestCounts.TryGetValue(symbol.Origin, out var count) ? count + 1 : 1;
            }
        }
        finally
        {
            foreach (var writer in writers.Values) writer.Dispose();
        }

        foreach (var source in sourceCounts.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var path = Path.Combine(skillsDir, source + ".md");
            manifestShards.TryGetValue(source, out var shards);
            shards ??= new List<string>();

            var content = new StringBuilder();
            content.AppendLine("# " + source);
            content.AppendLine();
            content.AppendLine("Symbols: " + (manifestCounts.TryGetValue(source, out var count) ? count : 0));
            content.AppendLine();

            if (shards.Count > 0)
            {
                content.AppendLine("## Shards");
                content.AppendLine();
                foreach (var shard in shards.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    content.AppendLine("- [" + shard + "](" + shard + ")");
            }

            File.WriteAllText(path, content.ToString());
        }
    }

    private static IEnumerable<Symbol> ReadSymbols(string symbolStream)
    {
        using var stream = OpenReadWithRetry(symbolStream);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var symbol = JsonSerializer.Deserialize<Symbol>(line);
            if (symbol is not null) yield return symbol;
        }
    }

    private static int SelectShard(string value, int shardCount)
    {
        var hash = StringComparer.Ordinal.GetHashCode(value);
        if (hash == int.MinValue) hash = 0;
        hash = Math.Abs(hash);
        return hash % shardCount;
    }

    private static FileStream OpenReadWithRetry(string filePath)
    {
        const int maxAttempts = 8;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(125 * attempt);
            }
        }

        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    }

    private static string Clean(string value) => value.Replace("\r", " ").Replace("\n", " ");
}
