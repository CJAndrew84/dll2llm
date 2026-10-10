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
    internal static void Run(string source, string output, string? sdk, string? catalogOutput = null)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        if (output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Output must be outside the product installation.");
        var parent = Path.GetDirectoryName(output) ?? throw new IOException("Invalid output path.");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, "." + Path.GetFileName(output) + ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            RunStages(source, stage, sdk);
            if (!string.IsNullOrWhiteSpace(catalogOutput))
            {
                catalogOutput = Path.GetFullPath(catalogOutput);
                if (catalogOutput == output || catalogOutput.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Catalogue output must be separate from analysis output.");
                var result = ProductCatalog.Run(["catalog", "--mode", "metadata", "--source", stage, "--output", catalogOutput]);
                if (result != 0) throw new InvalidDataException("Catalogue generation failed.");
            }
            Publish(stage, output);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private static void Publish(string stage, string output)
    {
        if (!Directory.Exists(output)) { Directory.Move(stage, output); return; }
        var backup = output + ".previous-" + Guid.NewGuid().ToString("N");
        Directory.Move(output, backup);
        try { Directory.Move(stage, output); }
        catch { Directory.Move(backup, output); throw; }
        try { Directory.Delete(backup, true); }
        catch (IOException) { Console.Error.WriteLine("Previous analysis retained at " + backup); }
    }

    private static void RunStages(string source, string output, string? sdk)
    {
        var stages = new List<Stage>();
        void StageRun(string name, Action action)
        {
            try { action(); stages.Add(new Stage(name, "success", null)); }
            catch (Exception ex) { stages.Add(new Stage(name, "failed", ex.Message)); throw new InvalidDataException("Stage " + name + " failed: " + ex.Message, ex); }
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
        StageRun("compose", () =>
        {
            var failures = AnalysisComposer.Compose(output);
            if (failures != 0) throw new InvalidDataException($"Composition had {failures} input errors; see composition-report.json");
        });
        StageRun("output-size-audit", () => OutputSizeAudit.Verify(output));
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
        OutputSizeAudit.Verify(output);
    }
}
