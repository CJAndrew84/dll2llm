using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DllToLLMDoc;

/// <summary>Counts recovered and unresolved reflection signatures in generated Markdown.</summary>
internal static class RecoveryAudit
{
    internal sealed record FileCounts(string Path, int Recovered, int Skipped);
    internal static void Write(string root, string output, string? baseline)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var content = File.ReadAllText(path);
                return new FileCounts(Path.GetRelativePath(root, path).Replace('\\', '/'),
                    Regex.Matches(content, @"\[RECOVERED: CLR metadata\]").Count,
                    Regex.Matches(content, @"\[SKIPPED (?:METHOD|PROPERTY|CONSTRUCTOR|EVENT|FIELD)\]").Count);
            }).ToArray();
        var recovered = files.Sum(x => x.Recovered);
        var skipped = files.Sum(x => x.Skipped);
        int? prior = null;
        if (baseline is not null)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(baseline));
            if (doc.RootElement.TryGetProperty("skipped", out var count))
                prior = count.GetInt32();
            else throw new InvalidDataException("Baseline must include integer 'skipped'.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            format = "dll2llm-recovery-audit-v1",
            recovered,
            skipped,
            baselineSkipped = prior,
            skippedDelta = prior.HasValue ? skipped - prior.Value : (int?)null,
            note = "Counts are Markdown markers, not unique signatures. Compare like-for-like corpora.",
            files
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Recovery audit: {recovered} recovered, {skipped} skipped" +
            (prior.HasValue ? $", delta {skipped - prior.Value:+#;-#;0}" : ""));
    }
}
