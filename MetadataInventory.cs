using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>
/// Reads CLR metadata without loading assemblies or invoking their code.
/// Raw signature blobs are preserved losslessly for subsequent type decoding.
/// </summary>
internal static class MetadataInventory
{
    internal static void WriteJson(string dll, string output)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null)
            throw new InvalidDataException("Input is not a managed CLR assembly: " + dll);
        var md = pe.GetMetadataReader();
        var types = new List<object>();
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            var name = md.GetString(type.Name);
            if (name == "<Module>") continue;
            var methods = new List<object>();
            foreach (var mh in type.GetMethods())
            {
                var method = md.GetMethodDefinition(mh);
                var parameters = method.GetParameters()
                    .Select(ph => md.GetParameter(ph))
                    .Where(p => p.SequenceNumber > 0)
                    .Select(p => new { sequence = p.SequenceNumber, name = md.GetString(p.Name) })
                    .ToArray();
                methods.Add(new
                {
                    name = md.GetString(method.Name),
                    attributes = method.Attributes.ToString(),
                    signatureHex = Convert.ToHexString(md.GetBlobBytes(method.Signature)),
                    parameters
                });
            }
            var fields = type.GetFields().Select(fh =>
            {
                var field = md.GetFieldDefinition(fh);
                return new { name = md.GetString(field.Name),
                    signatureHex = Convert.ToHexString(md.GetBlobBytes(field.Signature)) };
            }).ToArray();
            types.Add(new
            {
                @namespace = md.GetString(type.Namespace),
                name,
                attributes = type.Attributes.ToString(),
                methods,
                fields
            });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            source = Path.GetFullPath(dll),
            format = "raw-clr-metadata-v1",
            note = "Signatures are ECMA-335 blobs, not decoded C# signatures.",
            types
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Extracted CLR metadata for {types.Count} types to {output}");
    }
}
