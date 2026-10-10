using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>Converts Clang's JSON AST into searchable declaration records.</summary>
internal static class ClangAstNormalizer
{
    internal sealed record Declaration(string Kind, string QualifiedName, string? Type,
        string? Access, string? Source, string[] Parameters, string[] Bases);

    internal static IReadOnlyList<Declaration> Extract(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        var found = new List<Declaration>();
        Walk(document.RootElement, new List<string>(), null, source, found);
        return found;
    }

    private static void Walk(JsonElement node, List<string> scope, string? access,
        string source, List<Declaration> found)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        var kind = Read(node, "kind");
        var name = Read(node, "name");
        var isContainer = kind is "NamespaceDecl" or "CXXRecordDecl" or "ClassTemplateDecl"
            or "ClassTemplateSpecializationDecl" or "EnumDecl";
        var isRecord = kind is "CXXRecordDecl" or "ClassTemplateDecl"
            or "ClassTemplateSpecializationDecl";
        var isDeclaration = kind is "CXXRecordDecl" or "EnumDecl" or "FunctionDecl"
            or "CXXMethodDecl" or "CXXConstructorDecl" or "CXXDestructorDecl"
            or "FunctionTemplateDecl" or "FieldDecl" or "VarDecl";
        var implicitNode = node.TryGetProperty("isImplicit", out var implicitValue)
            && implicitValue.ValueKind == JsonValueKind.True;
        var complete = !isRecord || (node.TryGetProperty("completeDefinition", out var completed)
            && completed.ValueKind == JsonValueKind.True);
        var nextScope = new List<string>(scope);
        if (isDeclaration && !implicitNode && complete && !string.IsNullOrEmpty(name))
        {
            var fullName = string.Join("::", scope.Append(name));
            var parameters = new List<string>();
            if (node.TryGetProperty("inner", out var members) && members.ValueKind == JsonValueKind.Array)
                foreach (var member in members.EnumerateArray())
                    if (Read(member, "kind") == "ParmVarDecl")
                        parameters.Add((Read(member, "name") ?? "") + ":" + TypeName(member));
            var bases = new List<string>();
            if (node.TryGetProperty("bases", out var baseArray) && baseArray.ValueKind == JsonValueKind.Array)
                foreach (var item in baseArray.EnumerateArray())
                    bases.Add(TypeName(item) ?? "");
            found.Add(new Declaration(kind ?? "", fullName, TypeName(node), access,
                source, parameters.ToArray(), bases.ToArray()));
        }
        if (isContainer && !string.IsNullOrEmpty(name)) nextScope.Add(name);
        if (!node.TryGetProperty("inner", out var children) || children.ValueKind != JsonValueKind.Array) return;
        var currentAccess = isRecord
            ? (Read(node, "tagUsed") is "struct" or "union" ? "public" : "private")
            : access;
        foreach (var child in children.EnumerateArray())
        {
            if (Read(child, "kind") == "AccessSpecDecl")
            {
                currentAccess = Read(child, "access") ?? currentAccess;
                continue;
            }
            Walk(child, nextScope, currentAccess, source, found);
        }
    }

    private static string? TypeName(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("type", out var type)) return null;
        return Read(type, "qualType");
    }

    private static string? Read(JsonElement node, string property)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String) return null;
        return value.GetString();
    }
}
