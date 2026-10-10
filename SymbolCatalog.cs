using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DllToLLMDoc;

// Versioned adapters: never guess an object's meaning from a field name substring.
internal static class SymbolCatalog
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal sealed record Parameter(int Sequence, string Name, string? Type, string? Attributes, string? DefaultValue = null);
    internal sealed record Symbol(string Id, string Name, string Kind, string? Container,
        string Source, string Evidence, string? Signature)
    {
        public string? FullName { get; init; }
        public string? Namespace { get; init; }
        public string? Assembly { get; init; }
        public string? Visibility { get; init; }
        public string? Attributes { get; init; }
        public string? ReturnType { get; init; }
        public string? SignatureHex { get; init; }
        public string? SourcePointer { get; init; }
        public string? BaseType { get; init; }
        public string[] Interfaces { get; init; } = [];
        public string[] GenericParameters { get; init; } = [];
        public Parameter[] Parameters { get; init; } = [];
        public string[] Accessors { get; init; } = [];
        public string? Constant { get; init; }
        public long? Ordinal { get; init; }
        public long? Rva { get; init; }
        public string? Forwarder { get; init; }
        public string? Notes { get; init; }
        public string? Documentation { get; init; }
    }
    internal sealed record Extraction(string Format, string? Assembly, List<Symbol> Symbols, List<string> Diagnostics);

    internal static string Identity(params string?[] values) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();

    internal static Extraction Extract(JsonElement root, string source, bool native)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Metadata root must be an object.");
        var format = Text(root, "format") ?? Text(root, "kind");
        var symbols = new List<Symbol>();
        var diagnostics = new List<string>();
        var assembly = Path.GetFileNameWithoutExtension(source);
        if (format == "decoded-clr-metadata-v2" && !native)
        {
            var types = RequiredArray(root, "types");
            int ti = 0;
            foreach (var type in types.EnumerateArray())
            {
                var pointer = $"/types/{ti++}";
                var name = RequiredText(type, "name");
                var ns = Text(type, "namespace") ?? "";
                var full = ns.Length == 0 ? name : ns + "." + name;
                var attributes = Text(type, "attributes");
                var kind = attributes?.Split(',').Any(x => x.Trim() == "Interface") == true ? "interface" : "type";
                var note = attributes?.Contains("Nested", StringComparison.Ordinal) == true
                    ? "Legacy schema omits declaring-type identity; source pointer disambiguates this nested type." : null;
                symbols.Add(new Symbol(Identity(source, pointer, full), name, kind, null, source, format, null)
                { FullName = full, Namespace = ns, Assembly = assembly, Attributes = attributes, SourcePointer = pointer, Notes = note });
                foreach (var (collection, memberKind) in new[] { ("methods", "method"), ("properties", "property"), ("fields", "field"), ("events", "event") })
                {
                    if (!type.TryGetProperty(collection, out var members)) continue;
                    if (members.ValueKind != JsonValueKind.Array) throw new InvalidDataException(collection + " must be an array.");
                    int mi = 0;
                    foreach (var member in members.EnumerateArray())
                    {
                        var mp = $"{pointer}/{collection}/{mi++}";
                        var mn = RequiredText(member, "name");
                        var signature = Text(member, "decodedSignature");
                        var raw = Text(member, "signatureHex");
                        var parameters = new List<Parameter>();
                        if (member.TryGetProperty("parameters", out var ps))
                            foreach (var p in ps.EnumerateArray()) parameters.Add(new Parameter(
                                checked((int)(Number(p, "sequence") ?? 0)), Text(p, "name") ?? "", Text(p, "type"), Text(p, "attributes")));
                        symbols.Add(new Symbol(Identity(source, mp, mn, raw), mn,
                            mn is ".ctor" or ".cctor" ? "constructor" : memberKind, full, source, format, signature)
                        {
                            FullName = full + "." + mn, Namespace = ns, Assembly = assembly, SourcePointer = mp,
                            Attributes = Text(member, "attributes"), ReturnType = Text(member, "decodedType"),
                            SignatureHex = raw, Parameters = parameters.ToArray(),
                            Notes = "Legacy decoded signature is best-effort; retain raw signatureHex. Missing metadata is unknown, not absent."
                        });
                    }
                }
            }
        }
        else if (format == "pe-export-table" && native)
        {
            int i = 0;
            foreach (var export in RequiredArray(root, "exports").EnumerateArray())
            {
                var pointer = $"/exports/{i++}";
                var ordinal = Number(export, "ordinal") ?? throw new InvalidDataException("Export ordinal is missing.");
                var name = Text(export, "name") ?? $"ordinal:{ordinal}";
                symbols.Add(new Symbol(Identity(source, pointer, name), name, "export", null, source, format, null)
                {
                    FullName = name, Assembly = assembly, SourcePointer = pointer, Ordinal = ordinal,
                    Rva = Number(export, "rva"), Forwarder = Text(export, "forwarder"),
                    Notes = "Export-table evidence only. Calling convention, parameter types and C++ ownership are unknown."
                });
            }
        }
        else throw new InvalidDataException($"Unsupported metadata format '{format ?? "<missing>"}'. No heuristic symbol inference performed.");
        return new Extraction(format, assembly, symbols, diagnostics);
    }
    private static string RequiredText(JsonElement value, string name) => Text(value, name) ?? throw new InvalidDataException("Missing string: " + name);
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    private static long? Number(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.TryGetInt64(out var n) ? n : null;
    private static JsonElement RequiredArray(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array ? p : throw new InvalidDataException("Missing array: " + name);
}
