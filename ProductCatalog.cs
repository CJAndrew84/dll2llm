using System.Net;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using static DllToLLMDoc.SymbolCatalog;

namespace DllToLLMDoc;

internal static class ProductCatalog
{
    private const string Usage = "dll2llm catalog --source <folder-or-file> --output <separate-generated-folder> [--mode metadata|managed] [--include-nonpublic]\n" +
        "dll2llm catalog --source <catalogue-folder> --search <text> [--kind <kind>] [--limit 20] [--json]\n" +
        "dll2llm catalog --source <catalogue-folder> --validate";
    internal sealed record SourceRecord(string Source, long Bytes, string? Sha256, string Status, string? Format, string? Assembly, long Symbols);
    internal sealed record Diagnostic(string Source, string Status, string Message);

    public static int Run(string[] args)
    {
        try { return Execute(args); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or BadImageFormatException or OverflowException)
        { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    private static int Execute(string[] args)
    {
        string? source = null, output = null, search = null, kind = null;
        string mode = "metadata";
        int limit = 20;
        bool includeNonPublic = false, validate = false, json = false;
        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h") { Console.WriteLine(Usage); return 0; }
            if (arg == "--include-nonpublic") { includeNonPublic = true; continue; }
            if (arg == "--validate") { validate = true; continue; }
            if (arg == "--json") { json = true; continue; }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return BadUsage();
            var value = args[++i];
            switch (arg)
            {
                case "--source": source = value; break;
                case "--output": output = value; break;
                case "--mode": mode = value; break;
                case "--search": search = value; break;
                case "--kind": kind = value; break;
                case "--limit": if (!int.TryParse(value, out limit) || limit < 1 || limit > 1000) return BadUsage(); break;
                default: return BadUsage();
            }
        }
        if (string.IsNullOrWhiteSpace(source) || mode is not ("metadata" or "managed") || (includeNonPublic && mode != "managed")) return BadUsage();
        source = Path.GetFullPath(source);
        CatalogOutput.EnsureNoLinks(source);
        if (search != null || validate)
        {
            if (output != null || (search != null && validate) || !Directory.Exists(source)) return BadUsage();
            if (validate) { CatalogOutput.Validate(source); Console.WriteLine("Catalogue integrity, sizes, hashes and record counts passed."); return 0; }
            if (string.IsNullOrWhiteSpace(search)) return BadUsage();
            return Search(source, search!, kind, limit, json);
        }
        if (string.IsNullOrWhiteSpace(output) || (kind != null) || json || (!Directory.Exists(source) && !File.Exists(source))) return BadUsage();
        output = Path.GetFullPath(output);
        var sourceRoot = Directory.Exists(source) ? source : Path.GetDirectoryName(source)!;
        if (Overlap(sourceRoot, output)) throw new IOException("Source and output must not overlap. Choose a separate generated folder.");
        return Build(source, sourceRoot, output, mode, includeNonPublic);
    }
    private static int BadUsage() { Console.Error.WriteLine(Usage); return 2; }
    private static bool Overlap(string a, string b)
    {
        var compare = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)); b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        string Prefix(string p) => Path.EndsInDirectorySeparator(p) ? p : p + Path.DirectorySeparatorChar;
        return a.Equals(b, compare) || a.StartsWith(Prefix(b), compare) || b.StartsWith(Prefix(a), compare);
    }
    private static IEnumerable<string> Inputs(string source, string mode)
    {
        if (File.Exists(source)) return [source];
        var roots = mode == "metadata" ? new[] { "managed", "native" }.Select(x => Path.Combine(source, x)).Where(Directory.Exists).ToArray() : [];
        if (roots.Length == 0) roots = [source];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        return roots.SelectMany(root => Directory.EnumerateFiles(root, "*", options)).Where(p => mode == "managed"
            ? Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            : Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal);
    }
    private static int Build(string source, string sourceRoot, string output, string mode, bool includeNonPublic)
    {
        using var package = new CatalogOutput(output);
        var symbols = package.Open("symbols", ".jsonl", "symbols", CatalogOutput.ShardBytes);
        var sources = package.Open("sources", ".jsonl", "sources", CatalogOutput.ShardBytes);
        var problems = package.Open("diagnostics", ".jsonl", "diagnostics", CatalogOutput.ShardBytes);
        var docs = package.Open("reference", ".md", "reference", CatalogOutput.MarkdownBytes, "# API reference\n\n[Catalogue](README.md) | [All reference pages](reference-INDEX.md)\n\n");
        int scanned = 0, processed = 0, failed = 0, pointers = 0, nativeSkipped = 0;
        long total = 0;
        foreach (var path in Inputs(source, mode))
        {
            scanned++;
            var relative = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
            Extraction? extraction = null;
            long bytes = 0;
            string? hash = null;
            string? problem = null, status = null;
            try
            {
                CatalogOutput.EnsureNoLinks(path);
                bytes = new FileInfo(path).Length;
                hash = CatalogOutput.Hash(path);
                if (CatalogOutput.IsLfsPointer(path))
                { pointers++; status = "lfs-pointer"; problem = "Hydrate this source with git lfs pull; a pointer is not metadata."; }
                else if (mode == "managed")
                {
                    bool managed;
                    using (var f = File.OpenRead(path)) using (var pe = new PEReader(f)) managed = pe.HasMetadata;
                    if (!managed) { nativeSkipped++; status = "native-skipped"; problem = "Outside managed extraction scope; no CLR metadata. Native header/PDB extraction remains separate."; }
                    else extraction = ManagedMetadataExtractor.Extract(path, relative, includeNonPublic);
                }
                else
                {
                    using var f = File.OpenRead(path);
                    if (f.ReadByte() != 0xEF || f.ReadByte() != 0xBB || f.ReadByte() != 0xBF) f.Position = 0;
                    using var doc = JsonDocument.Parse(f);
                    bool native = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "pe-export-table";
                    extraction = SymbolCatalog.Extract(doc.RootElement, relative, native);
                }
                if (CatalogOutput.Hash(path) != hash) throw new IOException("Source changed during extraction; retry with a stable installation snapshot.");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or BadImageFormatException or ArgumentException or InvalidOperationException or OverflowException)
            { failed++; status = "failed"; problem = ex.GetType().Name + ": " + ex.Message; }
            if (status != null)
            {
                problems.Add(JsonSerializer.Serialize(new Diagnostic(relative, status, problem!), Json));
                sources.Add(JsonSerializer.Serialize(new SourceRecord(relative, bytes, hash, status, null, null, 0), Json));
                Console.Error.WriteLine(relative + ": " + problem);
                continue;
            }
            if (extraction == null) throw new InvalidDataException("Extractor produced no result.");
            processed++;
            if (extraction.Diagnostics.Count > 0) failed++;
            foreach (var message in extraction.Diagnostics)
            {
                problems.Add(JsonSerializer.Serialize(new Diagnostic(relative, "partial", message), Json));
                Console.Error.WriteLine(relative + ": " + message);
            }
            // Keep a type and its members together in the human reference; the hash ID is not a reading order.
            var ordered = extraction.Symbols.OrderBy(x => x.Namespace, StringComparer.Ordinal)
                .ThenBy(x => x.Container ?? x.FullName ?? x.Name, StringComparer.Ordinal)
                .ThenBy(x => x.Container == null ? 0 : 1).ThenBy(x => x.Name, StringComparer.Ordinal)
                .ThenBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Signature, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal);
            foreach (var symbol in ordered)
            {
                var human = CatalogOutput.Reference(symbol);
                if (symbol.ReturnType != null && symbol.Signature == null)
                    human += "Recorded type: <code>" + WebUtility.HtmlEncode(symbol.ReturnType) + "</code>\n\n";
                var reference = docs.Add(human) + "#" + symbol.Id;
                symbols.Add(JsonSerializer.Serialize(symbol with { Documentation = reference }, Json));
                total++;
            }
            sources.Add(JsonSerializer.Serialize(new SourceRecord(relative, bytes, hash, extraction.Diagnostics.Count == 0 ? "processed" : "partial",
                extraction.Format, extraction.Assembly, extraction.Symbols.Count), Json));
        }
        if (scanned == 0 || (processed == 0 && nativeSkipped > 0))
        {
            failed++;
            problems.Add(JsonSerializer.Serialize(new Diagnostic(".", "empty", "No matching supported inputs were processed."), Json));
        }
        var complete = failed == 0 && pointers == 0;
        if (!complete && File.Exists(Path.Combine(output, "catalog-manifest.json")) && CatalogOutput.ReadManifest(output).Complete)
            throw new InvalidDataException($"Incomplete extraction ({failed} failures, {pointers} LFS pointers). Previous complete catalogue preserved. Use a new output folder to retain detailed failure artifacts.");
        package.Finish(new CatalogOutput.Report(scanned, processed, total, failed, pointers, nativeSkipped, complete));
        Console.WriteLine($"Scanned {scanned}; processed {processed}; symbols {total}; failures {failed}; LFS pointers {pointers}; native skipped {nativeSkipped}; complete {complete}.");
        return complete ? 0 : 1;
    }
    private static int Search(string source, string query, string? kind, int limit, bool json)
    {
        var manifest = CatalogOutput.ReadManifest(source);
        if (!manifest.Complete) throw new InvalidDataException("Catalogue is incomplete; inspect diagnostics before using it for code generation.");
        int count = 0;
        foreach (var artifact in manifest.Artifacts.Where(x => x.Role == "symbols").OrderBy(x => x.Path, StringComparer.Ordinal))
        {
            var path = CatalogOutput.SafePath(source, artifact.Path);
            if (CatalogOutput.IsLfsPointer(path)) throw new InvalidDataException("Hydrate LFS symbol shards with git lfs pull before local search.");
            if (artifact.Bytes > CatalogOutput.MaximumBytes || new FileInfo(path).Length != artifact.Bytes || CatalogOutput.Hash(path) != artifact.Sha256)
                throw new InvalidDataException("Symbol shard failed integrity check: " + artifact.Path);
            foreach (var line in File.ReadLines(path))
            {
                var symbol = JsonSerializer.Deserialize<Symbol>(line, Json) ?? throw new InvalidDataException("Invalid symbol record.");
                if (kind != null && !symbol.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
                if (!(symbol.FullName ?? symbol.Name).Contains(query, StringComparison.OrdinalIgnoreCase) && !(symbol.Signature?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                Console.WriteLine(json ? JsonSerializer.Serialize(symbol, Json) : $"{symbol.FullName ?? symbol.Name} [{symbol.Kind}]\n  {symbol.Signature ?? "Signature unknown"}\n  {symbol.Source} ({symbol.Evidence})\n  {symbol.Documentation}");
                if (++count >= limit) return 0;
            }
        }
        if (count == 0 && !json) Console.WriteLine("No matches.");
        return 0;
    }
}
