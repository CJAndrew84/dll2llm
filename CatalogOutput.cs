using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static DllToLLMDoc.SymbolCatalog;

namespace DllToLLMDoc;

// Budgets are a project distribution policy, not a statement about GitHub's plan-dependent LFS limits.
internal sealed class CatalogOutput : IDisposable
{
    internal const long MaximumBytes = 95_000_000; // Strictly below the requested 100,000,000-byte ceiling.
    internal const int ShardBytes = 64 * 1024 * 1024;
    internal const int MarkdownBytes = 48_000;
    internal sealed record Artifact(string Path, string Role, long Bytes, string Sha256, long Records);
    internal sealed record Manifest(int SchemaVersion, bool Complete, long MaximumOutputBytes, List<Artifact> Artifacts);
    internal sealed record Report(int Scanned, int Processed, long Symbols, int Failed, int LfsPointers, int NativeSkipped, bool Complete);
    private readonly string target;
    internal readonly string Stage;
    private readonly List<Artifact> artifacts = [];
    private readonly List<Shards> writers = [];
    private bool published;

    internal CatalogOutput(string target)
    {
        this.target = Path.GetFullPath(target);
        EnsureNoLinks(this.target);
        if (Directory.Exists(this.target) && Directory.EnumerateFileSystemEntries(this.target).Any())
            Validate(this.target, requireComplete: false); // Refuse to overwrite user files or a modified generated catalogue.
        var parent = Path.GetDirectoryName(this.target) ?? throw new IOException("Output cannot be a filesystem root.");
        Directory.CreateDirectory(parent);
        Stage = Path.Combine(parent, "." + Path.GetFileName(this.target) + ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Stage);
    }
    internal Shards Open(string prefix, string extension, string role, long limit, string header = "")
    {
        var writer = new Shards(this, prefix, extension, role, limit, header);
        writers.Add(writer);
        return writer;
    }
    internal void Text(string name, string content, string role = "navigation")
    {
        var limit = name.EndsWith(".md", StringComparison.Ordinal) ? MarkdownBytes : MaximumBytes;
        var count = Encoding.UTF8.GetByteCount(content);
        if (count > limit) throw new InvalidDataException($"{name} exceeds its {limit}-byte budget.");
        var path = SafePath(Stage, name, mustExist: false);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        Register(name, role, 0);
    }
    private void Register(string name, string role, long records)
    {
        var path = SafePath(Stage, name);
        artifacts.Add(new Artifact(name, role, new FileInfo(path).Length, Hash(path), records));
    }
    internal void Finish(Report report)
    {
        foreach (var writer in writers) writer.Dispose();
        foreach (var role in new[] { "symbols", "sources", "diagnostics", "reference" })
            Navigation(role, artifacts.Where(x => x.Role == role).Select(x => x.Path).ToList());
        Text(".dll2llm-catalog", "dll2llm-catalog-v1\n", "metadata");
        Text(".gitattributes", "# Compact text remains readable through GitHub Contents API.\n*.md -filter diff merge text eol=lf\n*.jsonl filter=lfs diff=lfs merge=lfs -text\n*.json -filter diff merge text eol=lf\n.gitattributes -filter diff merge text eol=lf\n.dll2llm-catalog -filter diff merge text eol=lf\n", "metadata");
        Text("report.json", JsonSerializer.Serialize(report, Json) + "\n", "metadata");
        Text("README.md", $"# Product API catalogue\n\nSchema: 1. Complete: **{report.Complete}**.\n\n" +
            $"Scanned {report.Scanned}; processed {report.Processed}; symbols {report.Symbols}; failures {report.Failed}; LFS pointers {report.LfsPointers}; native binaries skipped {report.NativeSkipped}.\n\n" +
            "[Human-readable API reference](reference-INDEX.md) | [Machine-readable symbols](symbols-INDEX.md) | [Source provenance](sources-INDEX.md) | [Diagnostics](diagnostics-INDEX.md)\n\n" +
            "All files are limited to 95,000,000 bytes. JSONL shards target 64 MiB; Markdown pages are limited to 48,000 UTF-8 bytes.\n\n" +
            "## Evidence and limitations\n\nCLR signatures are read from metadata without loading target assemblies. Metadata access is not proof of public SDK support or runtime behaviour. Raw signatureHex is retained; display signatures use CLR notation, not guaranteed compilable C#. Native export records do not establish callable C++ contracts. Missing properties in legacy JSON mean unknown coverage, not absence. No vendor binaries are copied.\n\n" +
            "## Retrieval\n\nUse `dll2llm catalog --source <this-folder> --search <name> --limit 20` or navigate the reference pages. Use `--validate` instead of `--search` to verify sizes, hashes, JSONL record counts and catalogue completeness. Hydrate JSONL LFS objects with `git lfs pull` before local search. Markdown is directly readable without LFS.\n");
        Text("SKILL.md", "---\nname: product-api-catalog\ndescription: Retrieve versioned API symbols with source evidence and bounded human-readable references\n---\n\nStart with [README.md](README.md), then [reference](reference-INDEX.md). Preserve assembly/source identity and overloads. Treat unknown information as unknown. Never infer native signatures, supported SDK status or verified behaviour from names.\n");
        var manifest = new Manifest(1, report.Complete, MaximumBytes, artifacts.OrderBy(x => x.Path, StringComparer.Ordinal).ToList());
        // Manifest cannot include its own digest. It is size-checked but not added to its own artifact list.
        var json = JsonSerializer.Serialize(manifest, Json) + "\n";
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("Manifest exceeds output limit.");
        File.WriteAllText(Path.Combine(Stage, "catalog-manifest.json"), json, new UTF8Encoding(false));
        Validate(Stage, requireComplete: false);
        Publish();
    }
    private void Navigation(string role, List<string> pages)
    {
        int level = 0;
        while (pages.Count > 128)
        {
            var next = new List<string>();
            int part = 0;
            foreach (var group in pages.Chunk(128))
            {
                var name = $"{role}-index-{level:D2}-{++part:D5}.md";
                Text(name, "# " + role + " navigation\n\n" + string.Join("", group.Select(p => $"- [{p}]({p})\n")));
                next.Add(name);
            }
            pages = next;
            level++;
        }
        Text(role + "-INDEX.md", "# " + role + "\n\n" + (pages.Count == 0 ? "No records.\n" : string.Join("", pages.Select(p => $"- [{p}]({p})\n"))));
    }
    private void Publish()
    {
        if (!Directory.Exists(target)) { Directory.Move(Stage, target); published = true; return; }
        if (!Directory.EnumerateFileSystemEntries(target).Any()) { Directory.Delete(target); Directory.Move(Stage, target); published = true; return; }
        Validate(target, requireComplete: false);
        if (Hash(Path.Combine(Stage, "catalog-manifest.json")) == Hash(Path.Combine(target, "catalog-manifest.json")))
        { Directory.Delete(Stage, true); published = true; return; }
        var backup = target + ".previous-" + Guid.NewGuid().ToString("N");
        Directory.Move(target, backup);
        try { Directory.Move(Stage, target); published = true; }
        catch { if (!Directory.Exists(target)) Directory.Move(backup, target); throw; }
        try { Directory.Delete(backup, true); }
        catch (IOException) { Console.Error.WriteLine("Published; previous generated catalogue retained at " + backup); }
    }
    internal static Manifest ReadManifest(string directory)
    {
        EnsureNoLinks(directory);
        var path = SafePath(directory, "catalog-manifest.json");
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Manifest exceeds size budget.");
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<Manifest>(stream, Json) ?? throw new InvalidDataException("Invalid catalogue manifest.");
        if (manifest.SchemaVersion != 1 || manifest.MaximumOutputBytes != MaximumBytes || manifest.Artifacts == null)
            throw new InvalidDataException("Unsupported catalogue schema/policy.");
        return manifest;
    }
    internal static void Validate(string directory, bool requireComplete = true)
    {
        var manifest = ReadManifest(directory);
        var names = new HashSet<string>(StringComparer.Ordinal) { "catalog-manifest.json" };
        foreach (var item in manifest.Artifacts)
        {
            if (!names.Add(item.Path)) throw new InvalidDataException("Duplicate manifest path: " + item.Path);
            var path = SafePath(directory, item.Path);
            var limit = item.Path.EndsWith(".md", StringComparison.Ordinal) ? MarkdownBytes : MaximumBytes;
            if (item.Bytes > limit || new FileInfo(path).Length != item.Bytes || Hash(path) != item.Sha256)
                throw new InvalidDataException("Artifact size/hash mismatch: " + item.Path);
            if (IsLfsPointer(path)) throw new InvalidDataException("LFS object is not hydrated: " + item.Path);
            if (item.Path.EndsWith(".jsonl", StringComparison.Ordinal))
            {
                long count = 0;
                foreach (var line in File.ReadLines(path))
                {
                    using var parsed = JsonDocument.Parse(line);
                    count++;
                }
                if (count != item.Records) throw new InvalidDataException("JSONL record count mismatch: " + item.Path);
            }
        }
        if (File.ReadAllText(SafePath(directory, ".dll2llm-catalog")) != "dll2llm-catalog-v1\n")
            throw new InvalidDataException("Output ownership marker is missing/invalid.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            if (Directory.Exists(entry) || !names.Contains(Path.GetFileName(entry))) throw new InvalidDataException("Unmanaged output entry; refusing to overwrite: " + Path.GetFileName(entry));
        if (requireComplete && !manifest.Complete) throw new InvalidDataException("Catalogue is incomplete; inspect report.json and diagnostics.");
    }
    internal static string SafePath(string directory, string name, bool mustExist = true)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name != Path.GetFileName(name) || name.Contains('\\') || Path.IsPathRooted(name))
            throw new InvalidDataException("Unsafe catalogue path.");
        var path = Path.Combine(directory, name);
        EnsureNoLinks(path);
        if (mustExist && !File.Exists(path)) throw new InvalidDataException("Missing catalogue file: " + name);
        return path;
    }
    internal static void EnsureNoLinks(string path)
    {
        for (var p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symbolic links/reparse points are not accepted in catalogue paths.");
    }
    internal static bool IsLfsPointer(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(1024, stream.Length)];
        var count = stream.Read(buffer, 0, buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, count).TrimStart('\uFEFF').StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal);
    }
    internal static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant(); }
    internal static string Reference(Symbol symbol)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? "Unknown");
        var b = new StringBuilder($"<a id=\"{symbol.Id}\"></a>\n\n## {E(symbol.FullName ?? symbol.Name)}\n\n");
        b.Append("Kind: ").Append(E(symbol.Kind)).Append("; evidence: ").Append(E(symbol.Evidence)).Append("; visibility: ").Append(E(symbol.Visibility)).Append(".\n\n");
        b.Append("<pre>").Append(E(symbol.Signature ?? "No callable signature recorded.")).Append("</pre>\n\n");
        b.Append("Assembly: ").Append(E(symbol.Assembly)).Append("<br>Source: ").Append(E(symbol.Source)).Append("<br>Location: ").Append(E(symbol.SourcePointer)).Append("\n\n");
        if (symbol.Attributes != null) b.Append("Attributes: ").Append(E(symbol.Attributes)).Append("\n\n");
        if (symbol.BaseType != null) b.Append("Base type: ").Append(E(symbol.BaseType)).Append("\n\n");
        if (symbol.Interfaces.Length > 0) b.Append("Interfaces: ").Append(E(string.Join(", ", symbol.Interfaces))).Append("\n\n");
        if (symbol.GenericParameters.Length > 0) b.Append("Generic parameters: ").Append(E(string.Join(", ", symbol.GenericParameters))).Append("\n\n");
        foreach (var p in symbol.Parameters) b.Append("Parameter ").Append(p.Sequence).Append(": ").Append(E(p.Name)).Append("; type: ").Append(E(p.Type)).Append("; attributes: ").Append(E(p.Attributes)).Append("; default: ").Append(E(p.DefaultValue)).Append("<br>\n");
        if (symbol.Accessors.Length > 0) b.Append("\nAccessors: ").Append(E(string.Join("; ", symbol.Accessors))).Append("\n\n");
        if (symbol.Constant != null) b.Append("Constant (metadata encoding): ").Append(E(symbol.Constant)).Append("\n\n");
        if (symbol.Ordinal != null) b.Append("Export ordinal: ").Append(symbol.Ordinal).Append("; RVA: ").Append(symbol.Rva).Append("; forwarder: ").Append(E(symbol.Forwarder)).Append("\n\n");
        if (symbol.SignatureHex != null) b.Append("Signature blob: <code>").Append(E(symbol.SignatureHex)).Append("</code>\n\n");
        if (symbol.Notes != null) b.Append(E(symbol.Notes)).Append("\n\n");
        return b.Append("\n---\n").ToString();
    }
    public void Dispose()
    {
        foreach (var writer in writers) writer.Dispose();
        if (!published && Directory.Exists(Stage)) Directory.Delete(Stage, true);
    }
    internal sealed class Shards : IDisposable
    {
        private readonly CatalogOutput owner;
        private readonly string prefix, extension, role, header;
        private readonly long limit, headerBytes;
        private StreamWriter? writer;
        private string? name;
        private long bytes, records;
        private int part;
        private bool disposed;
        internal Shards(CatalogOutput owner, string prefix, string extension, string role, long limit, string header)
        {
            if (limit <= 0 || limit > MaximumBytes || (extension == ".md" && limit > MarkdownBytes)) throw new ArgumentOutOfRangeException(nameof(limit));
            this.owner = owner; this.prefix = prefix; this.extension = extension; this.role = role; this.limit = limit; this.header = header;
            headerBytes = Encoding.UTF8.GetByteCount(header);
            if (headerBytes >= limit) throw new ArgumentException("Header consumes shard budget.");
        }
        internal string Add(string record)
        {
            if (disposed) throw new ObjectDisposedException(nameof(Shards));
            var length = (long)Encoding.UTF8.GetByteCount(record) + 1; // Explicit LF, no BOM; measure actual encoded bytes.
            if (length + headerBytes > limit) throw new InvalidDataException("Single " + role + " record exceeds its shard budget; nothing is truncated.");
            if (writer != null && bytes + length > limit) Close();
            if (writer == null)
            {
                name = prefix + "-" + (++part).ToString("D5") + extension;
                writer = new StreamWriter(SafePath(owner.Stage, name, mustExist: false), false, new UTF8Encoding(false), 64 * 1024);
                writer.Write(header); bytes = headerBytes; records = 0;
            }
            writer.Write(record); writer.Write('\n'); bytes += length; records++;
            return name!;
        }
        private void Close()
        {
            if (writer == null) return;
            writer.Dispose(); writer = null;
            owner.Register(name!, role, records);
        }
        public void Dispose() { if (!disposed) { Close(); disposed = true; } }
    }
}
