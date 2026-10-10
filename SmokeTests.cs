using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace DllToLLMDoc;

internal static class SmokeTests
{
    internal static void Run()
    {
        var dll = typeof(SmokeTests).Assembly.Location;
        var temp = Path.Combine(Path.GetTempPath(), "dll2llm-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var metadata = Path.Combine(temp, "metadata.json");
            MetadataInventory.WriteJson(dll, metadata);
            var contents = File.ReadAllText(metadata);
            if (!contents.Contains("decodedSignature", StringComparison.Ordinal))
                throw new InvalidOperationException("Decoded signatures missing from metadata output.");
            var method = typeof(SmokeTests).GetMethod("Run", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            if (method is null || ReflectionSignatureRecovery.Method(method) is not string recovered ||
                !recovered.Contains("Run(", StringComparison.Ordinal))
                throw new InvalidOperationException("Metadata-token method recovery failed.");
            var property = typeof(BinaryInventory.Entry).GetProperty("Kind");
            if (property is null || ReflectionSignatureRecovery.Property(property) is not string recoveredProperty ||
                !recoveredProperty.Contains("Kind", StringComparison.Ordinal))
                throw new InvalidOperationException("Metadata-token property recovery failed.");
            var inventoryJson = Path.Combine(temp, "inventory.json");
            BinaryInventory.WriteJson(Path.GetDirectoryName(dll)!, inventoryJson);
            using (var inventoryDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(inventoryJson)))
            {
                var first = inventoryDocument.RootElement.GetProperty("binaries").EnumerateArray().First();
                _ = first.GetProperty("path").GetString();
                _ = first.GetProperty("kind").GetString();
            }
            var exportJson = Path.Combine(temp, "exports-from-managed.json");
            NativeExports.WriteJson(dll, exportJson);
            using (var exportDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(exportJson)))
            {
                foreach (var export in exportDocument.RootElement.GetProperty("exports").EnumerateArray())
                {
                    _ = export.GetProperty("ordinal").GetUInt32();
                    _ = export.GetProperty("rva").GetUInt32();
                }
            }
            var inventory = BinaryInventory.Scan(Path.GetDirectoryName(dll)!);
            if (!inventory.Any(e => e.Kind == "managed" && e.Path == dll))
                throw new InvalidOperationException("Managed assembly not discovered.");
            var sample = "{\"kind\":\"TranslationUnitDecl\",\"inner\":[{\"kind\":\"CXXRecordDecl\",\"name\":\"Example\",\"tagUsed\":\"struct\",\"completeDefinition\":true,\"inner\":[{\"kind\":\"FieldDecl\",\"name\":\"Value\",\"type\":{\"qualType\":\"int\"}}]}]}";
            var declarations = ClangAstNormalizer.Extract(sample, "selftest");
            if (!declarations.Any(d => d.QualifiedName == "Example::Value" && d.Access == "public"))
                throw new InvalidOperationException("C++ struct access parsing failed.");
            var headers = Path.Combine(temp, "headers.json");
            var exports = Path.Combine(temp, "exports.json");
            File.WriteAllText(headers, """{"headers":[{"header":"fixture.h","declarations":[{"qualifiedName":"Example::Value","kind":"FieldDecl","type":"int"}]}]}""");
            File.WriteAllText(exports, """{"exports":[{"name":"Value","ordinal":1}]}""");
            var output = Path.Combine(temp, "index");
            UnifiedApiIndex.Build(metadata, headers, exports, output);
            var indexFile = Path.Combine(output, "api-index.json");
            if (!File.Exists(indexFile) || !File.ReadAllText(indexFile).Contains("Example::Value"))
                throw new InvalidOperationException("Unified index fixture missing.");
            var correlation = Path.Combine(temp, "correlation.json");
            ApiCorrelation.Write(metadata, headers, exports, correlation);
            if (!File.Exists(correlation))
                throw new InvalidOperationException("Correlation output missing.");
            var fixtureDir = Path.Combine(temp, "manifest-fixture");
            Directory.CreateDirectory(fixtureDir);
            File.WriteAllText(Path.Combine(fixtureDir, "fixture.h"), "int example;");
            var firstManifest = Path.Combine(temp, "manifest-first.json");
            var secondManifest = Path.Combine(temp, "manifest-second.json");
            IncrementalManifest.Write(fixtureDir, firstManifest, null);
            IncrementalManifest.Write(fixtureDir, secondManifest, firstManifest);
            using (var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(secondManifest)))
            {
                if (manifest.RootElement.GetProperty("delta").GetProperty("changed").GetArrayLength() != 0)
                    throw new InvalidOperationException("Unchanged files were reported as changed.");
            }
            File.WriteAllText(Path.Combine(fixtureDir, "fixture.h"), "int changed;");
            var thirdManifest = Path.Combine(temp, "manifest-third.json");
            IncrementalManifest.Write(fixtureDir, thirdManifest, secondManifest);
            using (var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(thirdManifest)))
            {
                if (manifest.RootElement.GetProperty("delta").GetProperty("changed").GetArrayLength() != 1)
                    throw new InvalidOperationException("Changed file was not detected.");
            }
            var auditDir = Path.Combine(temp, "audit-fixture");
            Directory.CreateDirectory(auditDir);
            File.WriteAllText(Path.Combine(auditDir, "api.md"), "[SKIPPED METHOD] One\\n[RECOVERED: CLR metadata]\\n");
            var auditFile = Path.Combine(temp, "audit.json");
            RecoveryAudit.Write(auditDir, auditFile, null);
            using (var audit = System.Text.Json.JsonDocument.Parse(File.ReadAllText(auditFile)))
            {
                if (audit.RootElement.GetProperty("recovered").GetInt32() != 1 ||
                    audit.RootElement.GetProperty("skipped").GetInt32() != 1)
                    throw new InvalidOperationException("Recovery audit counts incorrect.");
            }
            var headerRoot = Path.Combine(temp, "headers-fixture");
            Directory.CreateDirectory(headerRoot);
            var one = Path.Combine(headerRoot, "one.h");
            var two = Path.Combine(headerRoot, "two.h");
            File.WriteAllText(one, "struct One {};");
            File.WriteAllText(two, "struct Two {};");
            var oldHeaderManifest = Path.Combine(temp, "old-header-manifest.json");
            IncrementalManifest.Write(headerRoot, oldHeaderManifest, null);
            var oldHeaderCatalogue = Path.Combine(temp, "old-headers.json");
            File.WriteAllText(oldHeaderCatalogue, System.Text.Json.JsonSerializer.Serialize(new {
                headers = new object[] { new { header = one, declarations = new object[0] },
                    new { header = two, declarations = new object[0] } }
            }));
            File.Delete(two);
            File.WriteAllText(one, "struct One { int value; };");
            var newHeaderManifest = Path.Combine(temp, "new-header-manifest.json");
            IncrementalManifest.Write(headerRoot, newHeaderManifest, oldHeaderManifest);
            var deltaCatalogue = Path.Combine(temp, "delta-headers.json");
            File.WriteAllText(deltaCatalogue, System.Text.Json.JsonSerializer.Serialize(new {
                headers = new object[] { new { header = one, declarations = new object[0] } }
            }));
            var mergedCatalogue = Path.Combine(temp, "merged-headers.json");
            HeaderCatalogueMerge.Merge(oldHeaderCatalogue, deltaCatalogue, newHeaderManifest, mergedCatalogue);
            using (var merged = System.Text.Json.JsonDocument.Parse(File.ReadAllText(mergedCatalogue)))
            {
                if (merged.RootElement.GetProperty("headers").GetArrayLength() != 1)
                    throw new InvalidOperationException("Deleted headers were not pruned.");
            }
            var combinedRoot = Path.Combine(temp, "combined");
            Directory.CreateDirectory(Path.Combine(combinedRoot, "managed"));
            Directory.CreateDirectory(Path.Combine(combinedRoot, "native"));
            File.Copy(metadata, Path.Combine(combinedRoot, "managed", "fixture.json"));
            File.Copy(exports, Path.Combine(combinedRoot, "native", "fixture.json"));
            File.Copy(headers, Path.Combine(combinedRoot, "sdk-headers.json"));
            if (AnalysisComposer.Compose(combinedRoot) != 0)
                throw new InvalidOperationException("Composition reported input failures.");
            if (!File.Exists(Path.Combine(combinedRoot, "index", "api-index.json")) ||
                !File.Exists(Path.Combine(combinedRoot, "skills", "SKILL.md")) ||
                !File.ReadAllText(Path.Combine(combinedRoot, "skills", "clang-ast.md")).Contains("Example::Value"))
                throw new InvalidOperationException("Automatic skill composition failed.");
            var docsRoot = Path.Combine(combinedRoot, "documentation");
            if (!File.Exists(Path.Combine(docsRoot, "README.md")) ||
                !File.Exists(Path.Combine(docsRoot, "SKILL.md")) ||
                !File.Exists(Path.Combine(docsRoot, "search", "api-index.json")) ||
                !File.Exists(Path.Combine(docsRoot, "domains", "other.md")) ||
                !Directory.EnumerateFiles(Path.Combine(docsRoot, "api"), "*.md").Any())
                throw new InvalidOperationException("Domain-organized documentation was not generated.");
            Console.WriteLine("PASS: metadata decoding, managed inventory, C++ AST normalization, unified index, correlation");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
