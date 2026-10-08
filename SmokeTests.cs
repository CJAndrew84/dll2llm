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
            Console.WriteLine("PASS: metadata decoding, managed inventory, C++ AST normalization, unified index, correlation");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
