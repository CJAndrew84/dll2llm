using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>One-command product inventory with optional SDK and native tooling.</summary>
internal static class ProductAnalyzer
{
    internal sealed record Stage(string Name, string Status, string? Detail);
    internal static void Run(string source, string output, string? sdk)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        Directory.CreateDirectory(output);
        var stages = new List<Stage>();
        void StageRun(string name, Action action)
        {
            try { action(); stages.Add(new Stage(name, "success", null)); }
            catch (Exception ex) { stages.Add(new Stage(name, "failed", ex.Message)); }
        }
        var inventoryFile = Path.Combine(output, "inventory.json");
        StageRun("inventory", () => BinaryInventory.WriteJson(source, inventoryFile));
        StageRun("manifest", () => IncrementalManifest.Write(source, Path.Combine(output, "manifest.json"), null));
        var managed = new List<string>();
        var native = new List<string>();
        if (File.Exists(inventoryFile))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(inventoryFile));
            foreach (var item in doc.RootElement.GetProperty("binaries").EnumerateArray())
            {
                var path = item.GetProperty("path").GetString();
                var kind = item.GetProperty("kind").GetString();
                if (path is null) continue;
                if (kind == "managed") managed.Add(path);
                else if (kind == "native") native.Add(path);
            }
        }
        var managedDir = Path.Combine(output, "managed");
        var nativeDir = Path.Combine(output, "native");
        Directory.CreateDirectory(managedDir);
        Directory.CreateDirectory(nativeDir);
        foreach (var path in managed)
        {
            var relative = Path.GetRelativePath(source, path);
            StageRun("metadata:" + relative, () =>
                MetadataInventory.WriteJson(path, Path.Combine(managedDir, relative + ".json")));
        }
        foreach (var path in native)
        {
            var relative = Path.GetRelativePath(source, path);
            StageRun("exports:" + relative, () =>
                NativeExports.WriteJson(path, Path.Combine(nativeDir, relative + ".json")));
        }
        if (sdk is not null)
        {
            StageRun("sdk-manifest", () => IncrementalManifest.Write(sdk, Path.Combine(output, "sdk-manifest.json"), null));
            StageRun("sdk-headers", () => CppSdkInventory.WriteHeaders(sdk, Path.Combine(output, "sdk-headers.json"),
                "clang++", new[] { sdk }));
            StageRun("sdk-import-libs", () => CppSdkInventory.WriteLibraries(sdk,
                Path.Combine(output, "sdk-libraries.json"), "llvm-readobj"));
        }
        File.WriteAllText(Path.Combine(output, "analysis-report.json"),
            JsonSerializer.Serialize(new
            {
                format = "product-analysis-v1",
                source = Path.GetFullPath(source),
                sdk = sdk is null ? null : Path.GetFullPath(sdk),
                managedAssemblies = managed.Count,
                nativeBinaries = native.Count,
                stages
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Analysis complete: {managed.Count} managed, {native.Count} native. " +
                          $"{stages.Count(x => x.Status == "failed")} failed stages.");
        if (stages.Any(x => x.Status == "failed")) Environment.ExitCode = 1;
    }
}
