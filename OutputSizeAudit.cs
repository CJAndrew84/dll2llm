using System;
using System.IO;
using System.Linq;

namespace DllToLLMDoc;

/// <summary>Distribution gate for every generated analyze artifact, including legacy stream writers.</summary>
internal static class OutputSizeAudit
{
    internal static void Verify(string root)
    {
        const long maximum = CatalogOutput.MaximumBytes;
        var failures = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Bytes: new FileInfo(path).Length))
            .Where(x => x.Bytes > maximum)
            .OrderByDescending(x => x.Bytes)
            .Take(20).ToArray();
        if (failures.Length > 0)
            throw new InvalidDataException("Generated files exceed the 95,000,000-byte publication limit: " +
                string.Join("; ", failures.Select(x => Path.GetRelativePath(root, x.Path) + " (" + x.Bytes + " bytes)")) +
                ". Split these outputs before publishing. No files have been silently truncated.");
    }
}
