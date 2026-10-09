using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>Publishes human-first domain navigation and evidence-backed AI reference pages.</summary>
internal static class DocumentationPublisher
{
    private static readonly (string Domain, string[] Terms)[] Rules =
    {
        ("civil-geometry", new[] { "alignment", "linear", "geometry", "corridor", "roadway", "rail", "profile", "superelevation" }),
        ("terrain", new[] { "terrain", "dtm", "surface", "tin" }),
        ("rules-and-dependencies", new[] { "rule", "dependency", "persist", "transaction", "objectspace" }),
        ("ec-and-data", new[] { "ecinstance", "ecschema", "ecclass", "ecobject", "dataaccess", "relationship" }),
        ("dgn-and-cad", new[] { "dgn", "microstation", "element", "cad", "modelref" }),
        ("ui-and-tools", new[] { "ui", "dialog", "tool", "command", "presentation" }),
        ("annotation", new[] { "annotation", "label", "dimension", "drawing" }),
        ("projectwise", new[] { "projectwise", "document", "repository" })
    };

    private sealed class DomainBucket
    {
        internal int Count;
        internal List<(string Name, string Origin)> Samples { get; } = new();
    }

    internal static void Publish(string outputRoot, string symbolStream)
    {
        var root = Path.Combine(outputRoot, "documentation");
        var domains = Path.Combine(root, "domains");
        var api = Path.Combine(root, "api");
        var relationships = Path.Combine(root, "relationships");
        var search = Path.Combine(root, "search");
        var reports = Path.Combine(root, "reports");
        var sources = Path.Combine(root, "sources");
        foreach (var dir in new[] { root, domains, api, relationships, search, reports, sources })
            Directory.CreateDirectory(dir);

        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in ReadSymbols(symbolStream))
            namespaces.Add(NamespaceOf(symbol.Name));

        var namespaceGroups = namespaces.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var apiLinks = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in namespaceGroups)
        {
            var slug = Slug(group);
            var unique = slug;
            for (var n = 2; !usedNames.Add(unique); n++) unique = slug + "-" + n;
            var file = unique + ".md";
            apiLinks[group] = file;
            using var writer = new StreamWriter(Path.Combine(api, file));
            writer.WriteLine("# " + group);
            writer.WriteLine();
            writer.WriteLine("Exact extracted records. Verify version and API support before calling native symbols.");
        }

        var apiBuffers = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var domainBuckets = new Dictionary<string, Dictionary<string, DomainBucket>>(StringComparer.Ordinal);
        foreach (var symbol in ReadSymbols(symbolStream))
        {
            var ns = NamespaceOf(symbol.Name);
            if (!apiLinks.TryGetValue(ns, out var apiFile)) continue;

            if (!apiBuffers.TryGetValue(apiFile, out var buffer))
            {
                buffer = new StringBuilder();
                apiBuffers[apiFile] = buffer;
            }

            buffer.AppendLine();
            buffer.AppendLine("## " + Clean(symbol.Name));
            buffer.AppendLine();
            buffer.AppendLine("- Kind: " + Clean(symbol.Kind));
            buffer.AppendLine("- Evidence: " + Clean(symbol.Origin));
            buffer.AppendLine("- Source: `" + Clean(symbol.Source).Replace("`", "'") + "`");
            if (symbol.Signature is not null)
                buffer.AppendLine("- Signature: `" + Clean(symbol.Signature).Replace("`", "'") + "`");

            if (buffer.Length > 65536)
            {
                File.AppendAllText(Path.Combine(api, apiFile), buffer.ToString());
                buffer.Clear();
            }

            foreach (var domain in Classify(symbol.Name))
            {
                if (!domainBuckets.TryGetValue(domain, out var namespaceMap))
                {
                    namespaceMap = new Dictionary<string, DomainBucket>(StringComparer.Ordinal);
                    domainBuckets[domain] = namespaceMap;
                }

                if (!namespaceMap.TryGetValue(ns, out var bucket))
                {
                    bucket = new DomainBucket();
                    namespaceMap[ns] = bucket;
                }

                bucket.Count++;
                if (bucket.Samples.Count < 150)
                    bucket.Samples.Add((symbol.Name, symbol.Origin));
            }
        }

        foreach (var entry in apiBuffers)
        {
            if (entry.Value.Length == 0) continue;
            File.AppendAllText(Path.Combine(api, entry.Key), entry.Value.ToString());
        }

        foreach (var domain in Rules.Select(r => r.Domain).Append("other"))
        {
            if (!domainBuckets.TryGetValue(domain, out var matches) || matches.Count == 0) continue;
            using var writer = new StreamWriter(Path.Combine(domains, domain + ".md"));
            writer.WriteLine("# " + domain.Replace('-', ' '));
            writer.WriteLine();
            writer.WriteLine("Automatic keyword classification. Entries may appear in multiple domains.");
            writer.WriteLine();
            foreach (var group in matches.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteLine("## " + group.Key);
                var link = "../api/" + apiLinks[group.Key];
                foreach (var item in group.Value.Samples)
                    writer.WriteLine("- [" + Clean(item.Name) + "](" + link + ") - " + item.Origin);
                if (group.Value.Count > 150) writer.WriteLine("- Further entries: " + link);
            }
        }

        var index = Path.Combine(outputRoot, "index", "api-index.json");
        if (File.Exists(index)) File.Copy(index, Path.Combine(search, "api-index.json"), true);
        var report = Path.Combine(outputRoot, "composition-report.json");
        if (File.Exists(report)) File.Copy(report, Path.Combine(reports, "composition-report.json"), true);
        File.WriteAllText(Path.Combine(sources, "README.md"),
            "# Provenance\n\nEach API entry identifies its extracted source and evidence type. " +
            "Managed metadata, Clang declarations and PE exports have different guarantees.\n");
        File.WriteAllText(Path.Combine(relationships, "README.md"),
            "# Relationships\n\nA relationship graph is not emitted without verified type edges. " +
            "Name-based correlations are hypotheses, not inheritance or call edges.\n");

        var domainLinks = Rules.Select(r => r.Domain).Append("other")
            .Where(d => File.Exists(Path.Combine(domains, d + ".md")))
            .Select(d => "- [" + d + "](domains/" + d + ".md)").ToArray();
        File.WriteAllText(Path.Combine(root, "README.md"),
            "# Product API knowledge base\n\nGenerated from managed CLR metadata, native PE exports and optional SDK headers.\n\n" +
            "## Browse by capability\n\n" + string.Join("\n", domainLinks) +
            "\n\n## Exact reference\n\nSee [API reference](api/) and [search index](search/api-index.json).\n\n" +
            "## Limitations\n\nClassification is heuristic; exports are not proof of supported native APIs.\n");
        File.WriteAllText(Path.Combine(root, "SKILL.md"),
            "---\nname: product-api-reference\ndescription: Evidence-backed managed and native SDK reference.\n---\n\n" +
            "# API lookup instructions\n\n1. Choose a capability from [domains](README.md).\n" +
            "2. Search [api-index.json](search/api-index.json) for exact symbol names.\n" +
            "3. Verify signatures and provenance in the [API reference](api/).\n" +
            "4. Do not invent methods, overloads, ABI contracts or relationships.\n" +
            "5. Treat native exports and name correlations as discovery evidence only.\n");
        File.WriteAllText(Path.Combine(root, "INDEX.md"),
            "# API navigation\n\n" + string.Join("\n", domainLinks) + "\n\n" +
            string.Join("\n", apiLinks.OrderBy(x => x.Key).Select(x => "- [" + x.Key + "](api/" + x.Value + ")")));
    }

    private static IEnumerable<AnalysisComposer.Symbol> ReadSymbols(string symbolStream)
    {
        foreach (var line in File.ReadLines(symbolStream))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var symbol = JsonSerializer.Deserialize<AnalysisComposer.Symbol>(line);
            if (symbol is not null) yield return symbol;
        }
    }

    private static string[] Classify(string name)
    {
        var value = name.ToLowerInvariant();
        var matches = Rules.Where(r => r.Terms.Any(value.Contains)).Select(r => r.Domain).ToArray();
        return matches.Length == 0 ? new[] { "other" } : matches;
    }
    private static string NamespaceOf(string name)
    {
        var normalized = name.Replace("::", ".");
        var index = normalized.LastIndexOf('.');
        return index < 0 ? "(global)" : normalized[..index];
    }
    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var value = new string(chars).Trim('-');
        return value.Length == 0 ? "global" : value.Length > 110 ? value[..110] : value;
    }
    private static string Clean(string value) => value.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
}
