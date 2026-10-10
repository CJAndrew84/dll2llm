using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DllToLLMDoc;
using static DllToLLMDoc.SymbolCatalog;

internal static class Program
{
    private static string root = "";
    private static int passed, failed;
    private const string Legacy = """
    {"format":"decoded-clr-metadata-v2","types":[{"namespace":"Example","name":"Widget","attributes":"Public","methods":[{"name":"Read","decodedSignature":"int Read(int)","signatureHex":"20010808","parameters":[{"sequence":1,"name":"value"}]},{"name":"Read","decodedSignature":"int Read(string)","signatureHex":"2001080E","parameters":[]}],"properties":[{"name":"Value","decodedType":"int","signatureHex":"280008"}],"fields":[{"name":"Flag","decodedType":"bool","signatureHex":"0602"}]}]}
    """;
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0])) { Console.Error.WriteLine("Provide the compiled Target.dll fixture path."); return 2; }
        root = Path.Combine(Path.GetTempPath(), "dll2llm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var isolated = Dir("isolated");
            var fixture = Path.Combine(isolated, "Target.dll");
            File.Copy(Path.GetFullPath(args[0]), fixture);
            var marker = Path.Combine(root, "executed.txt");
            Environment.SetEnvironmentVariable("DLL2LLM_FIXTURE_MARKER", marker);
            var extracted = ManagedMetadataExtractor.Extract(fixture, "Target.dll");
            Symbol Find(string name, string kind) => extracted.Symbols.First(s => s.Name == name && s.Kind == kind);
            Test("legacy v2 schema without kind fields", () => { using var doc = JsonDocument.Parse(Legacy); Equal(5, Extract(doc.RootElement, "managed/sample.json", false).Symbols.Count); });
            Test("legacy overload identity retained", () => { using var doc = JsonDocument.Parse(Legacy); var methods = Extract(doc.RootElement, "x.json", false).Symbols.Where(s => s.Kind == "method").ToArray(); Equal(2, methods.Select(s => s.Id).Distinct().Count()); True(methods.All(s => s.SignatureHex != null)); });
            Test("legacy parameters remain honest about missing type", () => { using var doc = JsonDocument.Parse(Legacy); var p = Extract(doc.RootElement, "x.json", false).Symbols.First(s => s.Parameters.Length > 0).Parameters[0]; Equal(1, p.Sequence); Equal("value", p.Name); True(p.Type == null); });
            Test("native exports do not become callable methods", () =>
            {
                using var doc = JsonDocument.Parse("{\"kind\":\"pe-export-table\",\"exports\":[{\"ordinal\":7,\"name\":null,\"rva\":1024,\"forwarder\":\"other.Func\"}]}");
                var symbol = Extract(doc.RootElement, "native/a.json", true).Symbols.Single(); Equal("export", symbol.Kind); Equal("ordinal:7", symbol.Name); True(symbol.Signature == null); Equal("other.Func", symbol.Forwarder);
            });
            Test("unknown schema is an error not false success", () => { using var doc = JsonDocument.Parse("{\"types\":[]}"); Throws<InvalidDataException>(() => Extract(doc.RootElement, "x.json", false)); });
            Test("managed extraction with dependency DLL absent", () => { True(!File.Exists(Path.Combine(isolated, "MissingDependency.dll"))); Equal(0, extracted.Diagnostics.Count); True(Find("External", "method").Signature!.Contains("ExternalContract.ExternalValue")); });
            Test("base type and interface references", () => { var t = Find("Widget`1", "class"); Equal("ExternalContract.ExternalBase", t.BaseType); True(t.Interfaces.Contains("ExternalContract.IExternal")); });
            Test("nested identity includes declaring type", () => True(Find("Nested", "class").FullName == "Fixture.Widget`1+Nested"));
            Test("generic parameters and return types", () => { var m = Find("Map", "method"); True(m.GenericParameters.Contains("U")); Equal("U", m.ReturnType); Equal("T", m.Parameters.Single().Type); });
            Test("generic constraints include flags and type constraints", () =>
            {
                var widget = Find("Widget`1", "class");
                True(widget.GenericConstraints.Any(x => x.Contains("T flags:") && x.Contains("ReferenceTypeConstraint")));
                var map = Find("Map", "method");
                True(map.GenericConstraints.Any(x => x.Contains("U flags:") && x.Contains("DefaultConstructorConstraint")));
            });
            Test("custom attributes resolve names rather than tokens only", () =>
            {
                var widget = Find("Widget`1", "class");
                True(widget.CustomAttributes.Any(x => x.Contains("System.ObsoleteAttribute")));
            });
            Test("assembly reference identity retained", () =>
            {
                var widget = Find("Widget`1", "class");
                True(widget.AssemblyReferences.Any(x => x.Contains("MissingDependency")));
            });
            Test("human reference displays rich metadata", () =>
            {
                var widget = Find("Widget`1", "class");
                var page = CatalogOutput.Reference(widget);
                True(page.Contains("Generic constraints:"));
                True(page.Contains("Custom attributes:"));
                True(page.Contains("Assembly references:"));
            });
            Test("property getter and private setter", () => { var p = Find("Value", "property"); True(p.Accessors.Contains("get:Public")); True(p.Accessors.Contains("set:Private")); });
            Test("readonly property and indexer", () => { Equal(1, Find("ReadOnly", "property").Accessors.Length); Equal(1, Find("Item", "property").Parameters.Length); });
            Test("enum struct interface delegate classification", () => { Find("Mode", "enum"); Find("Position", "struct"); Find("IContract", "interface"); Find("Callback", "delegate"); });
            Test("method overloads have distinct identities", () => Equal(2, extracted.Symbols.Where(x => x.Name == "Overload").Select(x => x.Id).Distinct().Count()));
            Test("optional values and enum constants preserve encoding", () => { True(Find("Optional", "method").Parameters[0].DefaultValue!.Contains("07000000")); True(Find("Answer", "field").Constant!.Contains("2A000000")); });
            Test("ref out array signatures and names", () => { var m = Find("Transform", "method"); Equal("System.Int32[]", m.ReturnType); True(m.Parameters[0].Type!.EndsWith("&")); True(m.Parameters[1].Attributes!.Contains("Out")); Equal("labels", m.Parameters[2].Name); });
            Test("effective visibility and opt-in internal inventory", () =>
            {
                True(!extracted.Symbols.Any(s => s.Name is "Hidden" or "HiddenNested" or "PublicInsideInternal"));
                var all = ManagedMetadataExtractor.Extract(fixture, "Target.dll", true); True(all.Symbols.Any(s => s.Name == "Hidden")); True(all.Symbols.Any(s => s.Name == "HiddenNested"));
            });
            Test("target assembly never loaded or executed", () => { True(!File.Exists(marker)); True(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Target")); });
            Test("UTF8 JSONL sharding preserves record boundaries", () =>
            {
                using var output = new CatalogOutput(Path.Combine(Dir("tiny-json"), "out"));
                using (var w = output.Open("tiny", ".jsonl", "symbols", 17)) { w.Add("\"éé\""); w.Add("\"éé\""); w.Add("\"éé\""); }
                var files = Directory.GetFiles(output.Stage, "*.jsonl"); Equal(2, files.Length); True(files.All(f => new FileInfo(f).Length <= 17)); Equal(3, files.Sum(f => File.ReadLines(f).Count()));
                foreach (var f in files) foreach (var line in File.ReadLines(f)) { using var json = JsonDocument.Parse(line); }
            });
            Test("Markdown size uses bytes including header and LF", () =>
            {
                using var output = new CatalogOutput(Path.Combine(Dir("tiny-md"), "out"));
                using (var w = output.Open("unicode", ".md", "reference", 32, "#\n")) { w.Add(new string('é', 14)); w.Add(new string('é', 14)); }
                var files = Directory.GetFiles(output.Stage, "*.md"); Equal(2, files.Length); True(files.All(f => new FileInfo(f).Length == 31));
            });
            Test("oversize record and unsafe hard budget rejected", () =>
            {
                using var output = new CatalogOutput(Path.Combine(Dir("limits"), "out"));
                using var w = output.Open("tiny", ".jsonl", "symbols", 64);
                Throws<InvalidDataException>(() => w.Add(new string('a', 64)));
                Throws<ArgumentOutOfRangeException>(() => output.Open("too-big", ".jsonl", "symbols", CatalogOutput.MaximumBytes + 1));
                True(CatalogOutput.MaximumBytes < 100_000_000);
            });
            Test("CLI metadata catalogue search validation round trip", () =>
            {
                var (src, dest) = Setup("round-trip"); Equal(0, Run(src, dest)); CatalogOutput.Validate(dest);
                var (code, text) = Capture(["catalog", "--source", dest, "--search", "Read", "--limit", "1", "--json"]); Equal(0, code);
                using var result = JsonDocument.Parse(text.Trim()); Equal("Read", result.RootElement.GetProperty("name").GetString()); True(result.RootElement.GetProperty("documentation").GetString()!.Contains("#"));
            });
            Test("deterministic regeneration does not rewrite output", () =>
            {
                var (src, dest) = Setup("repeat"); Equal(0, Run(src, dest)); var path = Path.Combine(dest, "README.md"); var hash = CatalogOutput.Hash(path); var time = File.GetLastWriteTimeUtc(path);
                Equal(0, Run(src, dest)); Equal(hash, CatalogOutput.Hash(path)); Equal(time, File.GetLastWriteTimeUtc(path));
            });
            Test("regeneration removes obsolete generated pages", () =>
            {
                var (src, dest) = Setup("shrink");
                File.WriteAllText(Path.Combine(src, "managed", "sample.json"), JsonSerializer.Serialize(new { format = "decoded-clr-metadata-v2", types = Enumerable.Range(0, 350).Select(i => new { name = "Type" + i, @namespace = "Example", attributes = "Public", methods = Array.Empty<object>(), fields = Array.Empty<object>() }) }));
                Equal(0, Run(src, dest)); var before = ReferencePages(dest).Length; True(before > 1);
                File.WriteAllText(Path.Combine(src, "managed", "sample.json"), Legacy); Equal(0, Run(src, dest)); Equal(1, ReferencePages(dest).Length); CatalogOutput.Validate(dest);
            });
            Test("LFS pointers and malformed inputs have distinct diagnostics", () =>
            {
                var (src, dest) = Setup("errors"); File.WriteAllText(Path.Combine(src, "managed", "pointer.json"), "\uFEFFversion https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 123\n"); File.WriteAllText(Path.Combine(src, "managed", "invalid.json"), "{broken");
                Equal(1, Run(src, dest)); using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(dest, "report.json"))); Equal(1, report.RootElement.GetProperty("lfsPointers").GetInt32()); Equal(1, report.RootElement.GetProperty("failed").GetInt32()); Throws<InvalidDataException>(() => CatalogOutput.Validate(dest));
            });
            Test("UTF8 BOM JSON input accepted", () => { var (src, dest) = Setup("bom"); File.WriteAllText(Path.Combine(src, "managed", "sample.json"), Legacy, new UTF8Encoding(true)); Equal(0, Run(src, dest)); });
            Test("empty input cannot report successful extraction", () => { var (src, dest) = Setup("empty"); File.Delete(Path.Combine(src, "managed", "sample.json")); Equal(1, Run(src, dest)); });
            Test("overlapping source output paths rejected", () => { var (src, _) = Setup("overlap"); Equal(1, Run(src, src)); Equal(1, Run(src, Path.Combine(src, "out"))); });
            Test("failed oversized record preserves previous catalogue", () =>
            {
                var (src, dest) = Setup("atomic"); Equal(0, Run(src, dest)); var before = CatalogOutput.Hash(Path.Combine(dest, "catalog-manifest.json"));
                File.WriteAllText(Path.Combine(src, "managed", "sample.json"), Legacy.Replace("int Read(int)", new string('λ', 48000))); Equal(1, Run(src, dest)); Equal(before, CatalogOutput.Hash(Path.Combine(dest, "catalog-manifest.json"))); CatalogOutput.Validate(dest);
            });
            Test("user files in output are not deleted", () => { var (src, dest) = Setup("owned"); Equal(0, Run(src, dest)); var note = Path.Combine(dest, "my-notes.txt"); File.WriteAllText(note, "keep"); Equal(1, Run(src, dest)); Equal("keep", File.ReadAllText(note)); });
            Test("tampered symbol shard fails validation", () => { var (src, dest) = Setup("tamper"); Equal(0, Run(src, dest)); File.AppendAllText(Directory.GetFiles(dest, "symbols-*.jsonl").Single(), "{}\n"); Throws<InvalidDataException>(() => CatalogOutput.Validate(dest)); });
            Test("manifest path traversal rejected", () => { Throws<InvalidDataException>(() => CatalogOutput.SafePath(root, "../secret")); Throws<InvalidDataException>(() => CatalogOutput.SafePath(root, "..\\secret")); });
            Test("Markdown encodes hostile names and signatures", () => { var s = new Symbol("safe", "<script>", "method", null, "a.dll", "clr-metadata", "<script>alert(1)</script>"); var md = CatalogOutput.Reference(s); True(!md.Contains("<script>")); True(md.Contains("&lt;script&gt;")); });
            Test("nested indexes remain within Markdown budget", () =>
            {
                var dest = Path.Combine(Dir("indexes"), "out"); using var output = new CatalogOutput(dest);
                var w = output.Open("reference", ".md", "reference", CatalogOutput.MarkdownBytes); for (int i = 0; i < 130; i++) w.Add(new string('a', 25000));
                output.Finish(new CatalogOutput.Report(1, 1, 0, 0, 0, 0, true)); True(Directory.GetFiles(dest, "reference-index-*.md").Length > 0); True(Directory.GetFiles(dest, "*.md").All(f => new FileInfo(f).Length <= CatalogOutput.MarkdownBytes)); CatalogOutput.Validate(dest);
            });
            Test("git attributes keep README plain and JSONL in LFS", () =>
            {
                var (src, dest) = Setup("attributes"); Equal(0, Run(src, dest)); var work = Path.GetDirectoryName(dest)!;
                Exec("git", work, "init", "-q"); var a = Exec("git", work, "check-attr", "filter", "--", "out/README.md", "out/symbols-00001.jsonl"); True(a.Contains("out/README.md: filter: unset")); True(a.Contains("out/symbols-00001.jsonl: filter: lfs"));
            });
            Test("managed CLI runs against isolated fixture", () => { var dest = Path.Combine(root, "managed-output"); Equal(0, Capture(["catalog", "--source", isolated, "--output", dest, "--mode", "managed"]).Code); CatalogOutput.Validate(dest); });
            Test("incomplete input cannot replace a complete catalogue", () =>
            {
                var (src, dest) = Setup("input-atomic"); Equal(0, Run(src, dest)); var before = CatalogOutput.Hash(Path.Combine(dest, "catalog-manifest.json"));
                File.WriteAllText(Path.Combine(src, "managed", "sample.json"), "{broken"); Equal(1, Run(src, dest)); Equal(before, CatalogOutput.Hash(Path.Combine(dest, "catalog-manifest.json"))); CatalogOutput.Validate(dest);
            });
            Test("unsupported CLI schema yields diagnostic exit code", () => { var (src, dest) = Setup("unsupported"); File.WriteAllText(Path.Combine(src, "managed", "sample.json"), "{\"format\":\"future-v99\",\"types\":[]}"); Equal(1, Run(src, dest)); True(!CatalogOutput.ReadManifest(dest).Complete); });
            Test("human reference groups type before members and retains legacy field type", () => { var (src, dest) = Setup("human"); Equal(0, Run(src, dest)); var md = File.ReadAllText(ReferencePages(dest).Single()); True(md.IndexOf("## Example.Widget\n", StringComparison.Ordinal) < md.IndexOf("## Example.Widget.Read", StringComparison.Ordinal)); True(md.Contains("Recorded type: <code>bool</code>")); });
        }
        finally { Environment.SetEnvironmentVariable("DLL2LLM_FIXTURE_MARKER", null); Directory.Delete(root, true); }
        Console.WriteLine($"RESULT: {passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
    private static void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e); } }
    private static string Dir(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static (string Source, string Destination) Setup(string name) { var dir = Dir(name); var source = Path.Combine(dir, "src"); Directory.CreateDirectory(Path.Combine(source, "managed")); File.WriteAllText(Path.Combine(source, "managed", "sample.json"), Legacy); return (source, Path.Combine(dir, "out")); }
    // '?' also matched INDEX.md; count actual numeric page names, not navigation files.
    private static string[] ReferencePages(string directory) => Directory.GetFiles(directory, "reference-*.md").Where(p => Path.GetFileNameWithoutExtension(p).Split('-').Last().All(char.IsDigit)).ToArray();
    private static int Run(string source, string dest) => Capture(["catalog", "--source", source, "--output", dest]).Code;
    private static (int Code, string Text) Capture(string[] args) { var old = Console.Out; using var text = new StringWriter(); try { Console.SetOut(text); return (ProductCatalog.Run(args), text.ToString()); } finally { Console.SetOut(old); } }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static string Exec(string file, string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var p = Process.Start(info)!; var text = p.StandardOutput.ReadToEnd(); var error = p.StandardError.ReadToEnd(); p.WaitForExit(); if (p.ExitCode != 0) throw new Exception(error); return text;
    }
}
