using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;

namespace DllToLLMDoc;

/// <summary>Lossless, bounded sharding of any oversized generated artifact, regardless of format.</summary>
internal static class OutputSizeAudit
{
    internal const long ChunkBytes = 64L * 1024 * 1024;
    internal sealed record Part(string File, long Bytes, string Sha256);
    internal sealed record ShardManifest(string Format, string Original, long Bytes, string Sha256, Part[] Parts);
    internal static void Shard(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).ToArray())
        {
            if (new FileInfo(path).Length <= CatalogOutput.MaximumBytes) continue;
            Split(path);
        }
        Verify(root);
    }
    private static void Split(string path)
    {
        var original = new FileInfo(path);
        var parts = new System.Collections.Generic.List<Part>();
        string manifestPath = path + ".parts.json";
        if (File.Exists(manifestPath)) throw new IOException("Existing shard manifest: " + manifestPath);
        try
        {
            using (var input = File.OpenRead(path))
            {
                var buffer = new byte[1024 * 1024];
                for (int n = 1; input.Position < input.Length; n++)
                {
                    string name = Path.GetFileName(path) + ".part-" + n.ToString("D5");
                    string output = Path.Combine(Path.GetDirectoryName(path)!, name);
                    if (File.Exists(output)) throw new IOException("Existing shard: " + output);
                    using var part = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
                    long remaining = Math.Min(ChunkBytes, input.Length - input.Position);
                    while (remaining > 0)
                    {
                        int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read == 0) throw new EndOfStreamException("Unexpected EOF");
                        part.Write(buffer, 0, read);
                        remaining -= read;
                    }
                    part.Flush(true);
                    parts.Add(new Part(name, part.Length, CatalogOutput.Hash(output)));
                }
            }
            var manifest = new ShardManifest("dll2llm-byte-shards-v1", Path.GetFileName(path),
                original.Length, CatalogOutput.Hash(path), parts.ToArray());
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
            VerifyParts(manifestPath);
            File.Delete(path);
        }
        catch
        {
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            foreach (var part in parts) { var p = Path.Combine(Path.GetDirectoryName(path)!, part.File); if (File.Exists(p)) File.Delete(p); }
            throw;
        }
    }
    internal static void VerifyParts(string manifestPath)
    {
        var m = JsonSerializer.Deserialize<ShardManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("Invalid shard manifest.");
        if (m.Format != "dll2llm-byte-shards-v1" || m.Parts.Length == 0) throw new InvalidDataException("Invalid shard format.");
        long size = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var p in m.Parts)
        {
            if (Path.GetFileName(p.File) != p.File) throw new InvalidDataException("Unsafe shard filename.");
            var file = Path.Combine(Path.GetDirectoryName(manifestPath)!, p.File);
            if (!File.Exists(file) || new FileInfo(file).Length != p.Bytes || p.Bytes > CatalogOutput.MaximumBytes || CatalogOutput.Hash(file) != p.Sha256)
                throw new InvalidDataException("Shard integrity failure: " + p.File);
            using var stream = File.OpenRead(file);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer, 0, read);
            size += p.Bytes;
        }
        if (size != m.Bytes || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != m.Sha256)
            throw new InvalidDataException("Reconstructed SHA-256 mismatch.");
    }
    internal static void Restore(string manifestPath, string destination)
    {
        VerifyParts(manifestPath);
        var m = JsonSerializer.Deserialize<ShardManifest>(File.ReadAllText(manifestPath))!;
        if (File.Exists(destination)) throw new IOException("Refusing to overwrite existing file.");
        string temp = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = File.Create(temp))
                foreach (var part in m.Parts)
                {
                    using var input = File.OpenRead(Path.Combine(Path.GetDirectoryName(manifestPath)!, part.File));
                    input.CopyTo(output);
                }
            if (CatalogOutput.Hash(temp) != m.Sha256) throw new InvalidDataException("Restored hash mismatch.");
            File.Move(temp, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void Verify(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(path).Length > CatalogOutput.MaximumBytes)
                throw new InvalidDataException("Output exceeds 95 MB: " + path);
            if (path.EndsWith(".parts.json", StringComparison.Ordinal)) VerifyParts(path);
        }
    }
}
