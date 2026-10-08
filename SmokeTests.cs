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
            Console.WriteLine("PASS: metadata decoding, managed inventory, C++ AST normalization");
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
