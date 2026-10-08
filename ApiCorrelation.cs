using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>
/// Evidence-preserving cross-index. Name matches are candidates, not proof of native interop.
/// </summary>
internal static class ApiCorrelation
{
    internal sealed record Symbol(string Name, string Kind, string Source, string Evidence);
    internal sealed record Match(Symbol Managed, Symbol Native, string MatchType, string Confidence);

    internal static void Write(string managedFile, string headersFile, string exportsFile, string output)
    {
        var managed = LoadManaged(managedFile);
        var native = LoadHeaders(headersFile).Concat(LoadExports(exportsFile)).ToArray();
        var nativeIndex = native.GroupBy(x => Normalize(x.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var matches = new List<Match>();
        foreach (var symbol in managed)
        {
            if (!nativeIndex.TryGetValue(Normalize(symbol.Name), out var candidates)) continue;
            foreach (var candidate in candidates)
                matches.Add(new Match(symbol, candidate, "normalized-name", "low"));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            format = "api-correlation-v1",
            warning = "Name-based matches are hypotheses, not proof of ABI or runtime relationships.",
            managedCount = managed.Length,
            nativeCount = native.Length,
            matches = matches.OrderBy(x => x.Managed.Name).ThenBy(x => x.Native.Name).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Correlated {managed.Length} managed and {native.Length} native symbols: {matches.Count} candidates.");
    }

    private static Symbol[] LoadManaged(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<Symbol>();
        foreach (var type in document.RootElement.GetProperty("types").EnumerateArray())
        {
            var ns = type.GetProperty("namespace").GetString();
            var name = type.GetProperty("name").GetString() ?? "";
            var qualified = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            list.Add(new Symbol(qualified, "managed-type", path, "clr-metadata"));
            foreach (var method in type.GetProperty("methods").EnumerateArray())
                list.Add(new Symbol(qualified + "." + method.GetProperty("name").GetString(),
                    "managed-method", path, "clr-metadata"));
        }
        return list.ToArray();
    }

    private static Symbol[] LoadHeaders(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<Symbol>();
        foreach (var header in document.RootElement.GetProperty("headers").EnumerateArray())
        {
            if (!header.TryGetProperty("declarations", out var declarations)) continue;
            foreach (var declaration in declarations.EnumerateArray())
                list.Add(new Symbol(declaration.GetProperty("qualifiedName").GetString() ?? "",
                    declaration.GetProperty("kind").GetString() ?? "cpp-declaration",
                    header.GetProperty("header").GetString() ?? path, "clang-ast"));
        }
        return list.ToArray();
    }

    private static Symbol[] LoadExports(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("exports").EnumerateArray()
            .Where(x => x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            .Select(x => new Symbol(x.GetProperty("name").GetString()!, "pe-export", path, "pe-export-table"))
            .ToArray();
    }

    private static string Normalize(string name)
    {
        var last = name.Replace("::", ".").Split('.').LastOrDefault() ?? "";
        return last.TrimStart('_').ToLowerInvariant();
    }
}
