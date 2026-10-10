using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>Runs LLVM symbol readers without executing inspected DLLs or PDB contents.</summary>
internal static class NativeDebugSymbols
{
    internal sealed record Result(string File, string Tool, int ExitCode, string Output, string Error);

    internal static void Inspect(string root, string output, string pdbutil, string undname)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = Enumerate(root).ToArray();
        var results = new List<Result>();
        foreach (var file in files)
        {
            // PDB parsing is best-effort; missing/private symbols must not be inferred.
            results.Add(Execute(pdbutil, new[] { "dump", "-publics", file }, file));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            format = "native-debug-symbols-v1",
            source = Path.GetFullPath(root),
            note = "Public PDB symbols are evidence only, not guaranteed callable API signatures.",
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void Demangle(string input, string output, string tool)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(input));
        var names = document.RootElement.GetProperty("exports").EnumerateArray()
            .Where(x => x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            .Select(x => x.GetProperty("name").GetString()!)
            .Where(x => x.StartsWith("?", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToArray();
        var results = names.Select(name => new
        {
            decorated = name,
            result = Execute(tool, new[] { name }, name)
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            format = "msvc-demangled-symbols-v1",
            source = Path.GetFullPath(input),
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files, children;
            try
            {
                files = Directory.GetFiles(dir, "*.pdb");
                children = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { continue; }
            foreach (var file in files) yield return file;
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static Result Execute(string tool, string[] args, string file)
    {
        try
        {
            var psi = new ProcessStartInfo(tool)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Cannot start " + tool);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                return new Result(file, tool, -1, "", "Timed out after 120 seconds");
            }
            return new Result(file, tool, process.ExitCode,
                stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        { return new Result(file, tool, -1, "", ex.Message); }
    }
}
