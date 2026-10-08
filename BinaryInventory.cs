using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>
/// Static PE inventory: never loads or executes inspected binaries.
/// Works for both native and managed Windows DLLs.
/// </summary>
internal static class BinaryInventory
{
    internal sealed record Entry(string Path, string Kind, string Machine, bool HasMetadata,
        string? Error);

    internal static IReadOnlyList<Entry> Scan(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);

        var results = new List<Entry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var pe = new PEReader(stream);
                if (pe.PEHeaders.PEHeader is null)
                {
                    results.Add(new Entry(path, "invalid-pe", "unknown", false, "Missing PE header"));
                    continue;
                }

                bool managed = pe.HasMetadata && pe.PEHeaders.CorHeader is not null;
                results.Add(new Entry(path, managed ? "managed" : "native",
                    pe.PEHeaders.CoffHeader.Machine.ToString(), pe.HasMetadata, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or BadImageFormatException or InvalidOperationException)
            {
                results.Add(new Entry(path, "unreadable", "unknown", false, ex.Message));
            }
        }
        return results;
    }

    internal static void WriteJson(string directory, string outputFile)
    {
        var entries = Scan(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
        File.WriteAllText(outputFile, JsonSerializer.Serialize(new
        {
            source = Path.GetFullPath(directory),
            generatedUtc = DateTimeOffset.UtcNow,
            binaries = entries
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Indexed {entries.Count} DLLs ({entries.Count(e => e.Kind == "native")} native, " +
                          $"{entries.Count(e => e.Kind == "managed")} managed).");
    }
}
