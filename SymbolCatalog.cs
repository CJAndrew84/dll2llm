using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>Reads product-analysis JSON without loading vendor binaries. Unknown schemas are retained as unclassified.</summary>
internal static class SymbolCatalog
{
    internal sealed record Symbol(string Id, string Name, string Kind, string? Container,
        string Source, string Evidence, string? Signature);
    internal static IReadOnlyList<Symbol> Extract(JsonElement root, string source, bool native)
    {
        var found = new Dictionary<string, Symbol>(StringComparer.Ordinal);
        Walk(root, null, 0);
        return found.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        void Walk(JsonElement node, string? container, int depth)
        {
            if (depth > 64) return;
            if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in node.EnumerateArray()) Walk(child, container, depth + 1);
                return;
            }
            if (node.ValueKind != JsonValueKind.Object) return;
            var kind = Value(node, "kind", "memberKind", "typeKind", "symbolKind");
            var name = Value(node, "fullName", "qualifiedName", "name");
            var signature = Value(node, "signature", "displaySignature", "declaration");
            var type = Value(node, "type", "returnType");
            string? next = container;
            if (name is not null && kind is not null)
            {
                string category = kind.ToLowerInvariant();
                bool accepted = new[] { "class", "struct", "interface", "enum", "delegate", "method", "property", "field", "event", "constructor", "function", "export" }
                    .Any(k => category.Contains(k, StringComparison.Ordinal));
                if (accepted)
                {
                    var evidence = native ? "native-metadata-unverified-signature" : "managed-metadata";
                    var id = string.Join("|", source, kind, container ?? "", name, signature ?? "");
                    found.TryAdd(id, new Symbol(id, name, kind, container, source, evidence, signature));
                    if (category.Contains("class") || category.Contains("struct") || category.Contains("interface") || category.Contains("enum"))
                        next = name;
                }
            }
            // A field called 'type' is not proof that an object is a class.
            foreach (var property in node.EnumerateObject())
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    Walk(property.Value, next, depth + 1);
        }
    }
    private static string? Value(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
            foreach (var p in obj.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
        return null;
    }
}
