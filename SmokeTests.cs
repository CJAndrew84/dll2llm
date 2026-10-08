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
            Console.WriteLine("PASS: metadata decoding, managed inventory, C++ AST normalization, unified index, correlation");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
