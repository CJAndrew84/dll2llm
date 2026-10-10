using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>Indexes managed dependencies recursively; never executes files during indexing.</summary>
internal sealed class AssemblyDependencyResolver : IDisposable
{
    private readonly Dictionary<string, List<(AssemblyName Identity, string Path)>> _index =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<object> _events = new();
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly ResolveEventHandler _handler;

    internal AssemblyDependencyResolver(IEnumerable<string> targets, IEnumerable<string> references)
    {
        var directories = targets.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!)
            .Concat(references.Select(Path.GetFullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                _events.Add(new { status = "missing-directory", directory });
                continue;
            }
            foreach (var file in EnumerateSafe(directory))
            {
                try
                {
                    var identity = AssemblyName.GetAssemblyName(file);
                    if (!_index.TryGetValue(identity.Name!, out var candidates))
                        _index[identity.Name!] = candidates = new();
                    candidates.Add((identity, file));
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException
                                             or FileNotFoundException or IOException or UnauthorizedAccessException)
                {
                    // Native DLLs and invalid assemblies are not managed dependency candidates.
                }
            }
        }
        _handler = Resolve;
        AppDomain.CurrentDomain.AssemblyResolve += _handler;
    }

    private static IEnumerable<string> EnumerateSafe(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if (!visited.Add(Path.GetFullPath(dir))) continue;
            string[] files, children;
            try
            {
                files = Directory.GetFiles(dir, "*.dll");
                children = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files) yield return file;
            foreach (var child in children)
            {
                // Avoid traversing directory links that could escape the requested tree or loop.
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        AssemblyName requested;
        try { requested = new AssemblyName(args.Name); }
        catch { return null; }
        if (requested.Name is null || !_active.Add(args.Name)) return null;
        try
        {
            if (!_index.TryGetValue(requested.Name, out var candidates))
            {
                _events.Add(new { status = "missing", requested = args.Name });
                return null;
            }
            // Strict identity matching prevents accidental cross-version Bentley loads.
            foreach (var candidate in candidates.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (!AssemblyName.ReferenceMatchesDefinition(requested, candidate.Identity)) continue;
                if (requested.Version is not null && requested.Version != candidate.Identity.Version) continue;
                try
                {
                    var loaded = Assembly.LoadFrom(candidate.Path);
                    _events.Add(new { status = "resolved", requested = args.Name, path = candidate.Path });
                    return loaded;
                }
                catch (Exception ex) when (ex is FileLoadException or FileNotFoundException
                                             or BadImageFormatException or IOException)
                {
                    _events.Add(new { status = "load-failed", requested = args.Name,
                        path = candidate.Path, error = ex.Message });
                }
            }
            _events.Add(new { status = "no-compatible-identity", requested = args.Name,
                candidates = candidates.Select(x => new { x.Path, version = x.Identity.Version?.ToString() }).ToArray() });
            return null;
        }
        finally { _active.Remove(args.Name); }
    }

    internal void WriteReport(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            indexedAssemblies = _index.Sum(x => x.Value.Count),
            resolutionEvents = _events
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose() => AppDomain.CurrentDomain.AssemblyResolve -= _handler;
}
