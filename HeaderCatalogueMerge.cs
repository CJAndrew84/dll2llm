using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>
/// Merges a changed-header scan with a previous full header catalogue.
/// The caller must provide the current manifest so removed headers are pruned.
/// </summary>
internal static class HeaderCatalogueMerge
{
    internal static void Merge(string previous, string delta, string manifest, string output)
    {
        using var oldDoc = JsonDocument.Parse(File.ReadAllText(previous));
        using var deltaDoc = JsonDocument.Parse(File.ReadAllText(delta));
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifest));
        var root = manifestDoc.RootElement.GetProperty("source").GetString()
            ?? throw new InvalidDataException("Manifest source missing");
        var allowed = manifestDoc.RootElement.GetProperty("files").EnumerateArray()
            .Where(x => x.GetProperty("error").ValueKind == JsonValueKind.Null)
            .Select(x => x.GetProperty("path").GetString() ?? "")
            .Where(x => x.EndsWith(".h", StringComparison.OrdinalIgnoreCase)
                     || x.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase))
            .Select(x => Path.GetFullPath(Path.Combine(root, x.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = manifestDoc.RootElement.GetProperty("delta").GetProperty("changed")
            .EnumerateArray()
            .Select(x => Path.GetFullPath(Path.Combine(root,
                (x.GetString() ?? "").Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var records = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in oldDoc.RootElement.GetProperty("headers").EnumerateArray())
        {
            var path = Path.GetFullPath(header.GetProperty("header").GetString() ?? "");
            if (allowed.Contains(path) && !changed.Contains(path))
                records[path] = header.Clone();
        }
        foreach (var header in deltaDoc.RootElement.GetProperty("headers").EnumerateArray())
        {
            var path = Path.GetFullPath(header.GetProperty("header").GetString() ?? "");
            if (allowed.Contains(path))
                records[path] = header.Clone();
        }
        var missing = changed.Where(x => allowed.Contains(x) && !records.ContainsKey(x)).ToArray();
        if (missing.Length != 0)
            throw new InvalidDataException("Delta scan omitted changed headers: " + string.Join(", ", missing.Take(10)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            format = "clang-declarations-v2",
            source = root,
            incremental = true,
            headers = records.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Value).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Merged {records.Count} current headers.");
    }
}
