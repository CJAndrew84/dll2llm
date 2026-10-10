using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DllToLLMDoc;

/// <summary>Exact metadata-token fallback for reflection members with missing signature dependencies.</summary>
internal static class ReflectionSignatureRecovery
{
    internal static string? Method(MethodInfo method)
    {
        try
        {
            using var file = File.OpenRead(method.Module.FullyQualifiedName);
            using var pe = new PEReader(file);
            var reader = pe.GetMetadataReader();
            var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken);
            var definition = reader.GetMethodDefinition(handle);
            var signature = definition.DecodeSignature(new MetadataTypeProvider(reader), null);
            var names = definition.GetParameters()
                .Select(h => reader.GetParameter(h))
                .Where(p => p.SequenceNumber > 0)
                .ToDictionary(p => (int)p.SequenceNumber, p => reader.GetString(p.Name));
            var parameters = signature.ParameterTypes.Select((p, i) =>
                p + (names.TryGetValue(i + 1, out var name) && name.Length > 0 ? " " + name : ""));
            return (method.IsStatic ? "static " : "") + signature.ReturnType + " " +
                reader.GetString(definition.Name) + "(" + string.Join(", ", parameters) + ")";
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException
                                   or InvalidOperationException or NotSupportedException)
        { return null; }
    }

    internal static string? Property(PropertyInfo property)
    {
        try
        {
            using var file = File.OpenRead(property.Module.FullyQualifiedName);
            using var pe = new PEReader(file);
            var reader = pe.GetMetadataReader();
            var handle = MetadataTokens.PropertyDefinitionHandle(property.MetadataToken);
            var definition = reader.GetPropertyDefinition(handle);
            var signature = definition.DecodeSignature(new MetadataTypeProvider(reader), null);
            var accessors = definition.GetAccessors();
            var getter = !accessors.Getter.IsNil;
            var setter = !accessors.Setter.IsNil;
            return signature.ReturnType + " " + reader.GetString(definition.Name) +
                " { " + (getter ? "get; " : "") + (setter ? "set; " : "") + "}";
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ArgumentException
                                   or InvalidOperationException or NotSupportedException)
        { return null; }
    }
}
