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
        foreach (var path in EnumerateDlls(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
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

    private static IEnumerable<string> EnumerateDlls(string root)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            if (!visited.Add(Path.GetFullPath(directory))) continue;
            string[] files;
            string[] children;
            try
            {
                files = Directory.GetFiles(directory, "*.dll");
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Skipping inaccessible directory {directory}: {ex.Message}");
                continue;
            }
            foreach (var file in files) yield return file;
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Skipping inaccessible directory {child}: {ex.Message}");
                }
            }
        }
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
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine($"Indexed {entries.Count} DLLs ({entries.Count(e => e.Kind == "native")} native, " +
                          $"{entries.Count(e => e.Kind == "managed")} managed).");
    }
}
