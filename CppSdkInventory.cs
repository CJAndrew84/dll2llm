using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DllToLLMDoc;

/// <summary>
/// C++ SDK declarations via an external Clang AST dump and COFF import library inventory
/// via llvm-readobj. Tools are subprocesses with explicit arguments, never arbitrary shell commands.
/// </summary>
internal static class CppSdkInventory
{
    internal static void WriteHeaders(string root, string output, string clangPath, string[] includes, string? deltaManifest = null)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var changed = ReadDelta(deltaManifest);
        var headers = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".h", StringComparison.OrdinalIgnoreCase) ||
                        p.EndsWith(".hpp", StringComparison.OrdinalIgnoreCase)) &&
                        (changed is null || changed.Contains(Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new List<object>();
        foreach (var header in headers)
        {
            var args = new List<string> { "-x", "c++", "-fsyntax-only", "-Xclang", "-ast-dump=json" };
            foreach (var include in includes) { args.Add("-I"); args.Add(include); }
            args.Add(header);
            try
            {
                var run = Execute(clangPath, args);
                // Preserve AST as JSON text; do not invent declarations if dependencies are missing.
                var declarations = new List<ClangAstNormalizer.Declaration>();
                if (!string.IsNullOrWhiteSpace(run.stdout))
                {
                    try { declarations.AddRange(ClangAstNormalizer.Extract(run.stdout, header)); }
                    catch (JsonException ex) { results.Add(new { header, exitCode = -2, stderr = "Invalid AST JSON: " + ex.Message, declarations }); continue; }
                }
                results.Add(new { header, run.exitCode, run.stderr, declarations });
            }
            catch (Exception ex)
            {
                results.Add(new { header, exitCode = -1, stderr = ex.Message, declarations = new List<ClangAstNormalizer.Declaration>() });
            }
        }
        Write(output, new { format = "clang-declarations-v2", source = Path.GetFullPath(root), headers = results });
    }

    internal static void WriteLibraries(string root, string output, string readobjPath)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var libs = Directory.EnumerateFiles(root, "*.lib", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new List<object>();
        foreach (var lib in libs)
        {
            try
            {
                var run = Execute(readobjPath, new[] { "--coff-imports", "--symbols", lib });
                results.Add(new { library = lib, run.exitCode, run.stderr, symbols = run.stdout });
            }
            catch (Exception ex)
            {
                results.Add(new { library = lib, exitCode = -1, stderr = ex.Message, symbols = "" });
            }
        }
        Write(output, new { format = "llvm-coff-symbols-v1", source = Path.GetFullPath(root), libraries = results });
    }

    private static HashSet<string>? ReadDelta(string? manifest)
    {
        if (manifest is null) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        return doc.RootElement.GetProperty("delta").GetProperty("changed")
            .EnumerateArray().Select(x => x.GetString() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static (int exitCode, string stdout, string stderr) Execute(string tool, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch " + tool);
        // Consume both streams concurrently to avoid pipe-buffer deadlocks.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Tool exceeded 120 seconds: " + tool);
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
