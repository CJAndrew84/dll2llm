using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>Produces a deterministic file manifest and compares it with a prior scan.</summary>
internal static class IncrementalManifest
{
    internal sealed record FileEntry(string Path, long Size, string Sha256, string? Error);
    internal static void Write(string directory, string output, string? previous)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var root = Path.GetFullPath(directory);
        var entries = new List<FileEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files, children;
            try { files = Directory.GetFiles(dir); children = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files.Where(f => new[] { ".dll", ".pdb", ".lib", ".h", ".hpp" }
                         .Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                try
                {
                    using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var hash = Convert.ToHexString(SHA256.HashData(stream));
                    entries.Add(new FileEntry(relative, stream.Length, hash, null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { entries.Add(new FileEntry(relative, 0, "", ex.Message)); }
            }
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        var sorted = entries.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var old = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (previous is not null)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(previous));
            foreach (var item in doc.RootElement.GetProperty("files").EnumerateArray())
                old[item.GetProperty("path").GetString()!] = item.GetProperty("sha256").GetString() ?? "";
        }
        var now = sorted.ToDictionary(x => x.Path, x => x.Sha256, StringComparer.OrdinalIgnoreCase);
        var changed = sorted.Where(x => !old.TryGetValue(x.Path, out var hash) || hash != x.Sha256)
            .Select(x => x.Path).ToArray();
        var deleted = old.Keys.Where(p => !now.ContainsKey(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            format = "dll2llm-file-manifest-v1",
            source = root,
            files = sorted.Select(x => new { path = x.Path, size = x.Size, sha256 = x.Sha256, error = x.Error }),
            delta = new { changed, deleted }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Manifest: {sorted.Length} files, {changed.Length} new/changed, {deleted.Length} deleted.");
    }
}
