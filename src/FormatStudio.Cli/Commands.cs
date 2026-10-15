using System.Text.Json.Nodes;
using FormatStudio.Codegen;
using FormatStudio.Dsl;
using FormatStudio.Importers;
using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Simulation;

namespace FormatStudio.Cli;

internal static class Commands
{
    private static readonly string[] NoFlags = [];

    private static FormatProject LoadProject(CommandLine line)
    {
        var path = line.Single("project") ?? FindProject(Directory.GetCurrentDirectory())
            ?? throw new UsageException($"No *{ProjectSettings.Extension} found; pass --project.");
        var project = FormatProject.Load(path);
        foreach (var diagnostic in project.Diagnostics.Where(d => d.Severity == Severity.Error).Take(20))
            Console.Error.WriteLine(diagnostic);
        return project;
    }

    private static string? FindProject(string directory)
    {
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
        {
            var files = current.GetFiles("*" + ProjectSettings.Extension);
            if (files.Length == 1) return files[0].FullName;
            if (files.Length > 1) throw new UsageException($"Several project files in {current.FullName}; pass --project.");
        }

        return null;
    }

    private static byte[] InputBytes(CommandLine line)
    {
        if (line.Single("hex") is { } hex) return Hex.Parse(hex);
        if (line.Single("file") is { } file) return File.ReadAllBytes(file);
        throw new UsageException("Pass --hex <hex> or --file <file>.");
    }

    private static void PrintDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics) Console.Error.WriteLine(diagnostic);
    }

    // ------------------------------------------------------------------ project

    public static int New(string[] args)
    {
        var line = CommandLine.Parse(args, ["name", "endian", "length"], NoFlags);
        if (line.Positional.Count != 1) throw new UsageException("Usage: format-studio new <folder>");
        var file = ProjectTemplate.Create(line.Positional[0], line.Single("name"), line.Single("endian") ?? "little", line.Single("length") ?? "varuint32");
        Console.WriteLine($"Created {file}");
        return 0;
    }

    public static int Import(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
            throw new UsageException($"Usage: format-studio import <importer> [options] ({string.Join(", ", ImporterRegistry.All.Select(i => i.Name))})");
        var importer = ImporterRegistry.Find(args[0]);
        var line = CommandLine.Parse(args[1..], ["project", .. importer.Options.Select(o => o.CliName)], ["dry-run"]);
        line.MaxPositional(0, $"format-studio import {importer.Name} [options]");
        var project = LoadProject(line);

        // Paths on the command line are relative to the current folder, paths in the project to the project folder.
        var settings = ImporterRegistry.Settings(project, importer.Name);
        foreach (var option in importer.Options)
        {
            var values = line.All(option.CliName).Select(v => option.IsPath ? Path.GetFullPath(v) : v).ToList();
            if (values.Count == 0) continue;
            if (!option.Multiple && values.Count > 1) throw new UsageException($"Option --{option.CliName} may only be given once.");
            settings[option.Key] = option.Multiple ? new JsonArray(values.Select(v => (JsonNode)v!).ToArray()) : values[0];
        }

        var dryRun = line.Has("dry-run");
        var result = importer.Run(project, settings, !dryRun);
        PrintDiagnostics(result.Diagnostics.Take(50));
        Console.WriteLine($"{result.Definitions.Count} definitions: {result.Written.Count} written, {result.Unchanged} unchanged, " +
                          $"{result.Deleted.Count} deleted{(dryRun ? " (dry run, nothing written)" : "")}.");
        if (result.ReportFile != null) Console.WriteLine($"Report: {result.ReportFile}");
        return result.Diagnostics.HasErrors ? 1 : 0;
    }

    public static int Validate(string[] args)
    {
        var line = CommandLine.Parse(args, ["project"], ["verbose"]);
        var project = LoadProject(line);
        var shown = project.Diagnostics.Where(d => line.Has("verbose") || d.Severity != Severity.Info).ToList();
        foreach (var diagnostic in shown.Where(d => d.Severity != Severity.Error)) Console.WriteLine(diagnostic);
        Console.WriteLine($"{project.Types.Count} definitions, {project.Packets.Count()} packets, {project.Samples.Count} samples: " +
                          $"{project.Diagnostics.Count(d => d.Severity == Severity.Error)} errors, " +
                          $"{project.Diagnostics.Count(d => d.Severity == Severity.Warning)} warnings.");
        return project.Diagnostics.HasErrors ? 1 : 0;
    }

    public static int List(string[] args)
    {
        var line = CommandLine.Parse(args, ["project", "kind", "filter"], NoFlags);
        var project = LoadProject(line);
        var kind = line.Single("kind");
        var filter = line.Single("filter");
        foreach (var definition in project.Types.Values
                     .Where(d => kind == null || d.Kind == kind)
                     .Where(d => filter == null || d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(d => d is StructDef { IsPacket: true } p ? p.Id : long.MaxValue)
                     .ThenBy(d => d.Name, StringComparer.Ordinal))
        {
            var detail = definition switch
            {
                StructDef { IsPacket: true } packet => $"id {packet.Id}, {packet.Fields.Count} fields",
                StructDef structDef => $"{structDef.Fields.Count} fields",
                EnumDef enumDef => $"{enumDef.Members.Count} members, {enumDef.Type}",
                VariantDef variant => $"{variant.Cases.Count} cases, tag {variant.Tag?.ToString() ?? "external"}",
                AliasDef alias => $"= {TypeExpressions.TryFormat(alias.Target) ?? alias.Target.ToString()}",
                BitfieldDef bitfield => $"{bitfield.Members.Count} members in {bitfield.Base}",
                _ => "",
            };
            Console.WriteLine($"{definition.Kind,-8} {definition.Name,-50} {detail}");
        }

        return 0;
    }

    public static int Show(string[] args)
    {
        var line = CommandLine.Parse(args, ["project", "target"], ["csharp"]);
        if (line.Positional.Count != 1) throw new UsageException("Usage: format-studio show <type> [--target <target|generator>] [--csharp]");
        var project = LoadProject(line);
        var definition = project.Find(line.Positional[0]) ?? throw new UsageException($"Unknown type '{line.Positional[0]}'.");
        var targetName = line.Single("target") ?? (line.Has("csharp") ? "csharp" : null);
        if (targetName == null)
        {
            Console.Write(DslWriter.Write(definition));
            return 0;
        }

        var target = FindTarget(project, targetName);
        var output = CodeGeneration.Generate(project, target);
        var file = output.FileOf(definition.Name);
        if (file == null)
        {
            Console.Error.WriteLine($"No {target.Generator} file for {definition.Name} (aliases are inlined, definitions with skipCodegen are skipped).");
            PrintDiagnostics(output.Diagnostics.Where(d => d.Message.StartsWith(definition.Name + ":", StringComparison.Ordinal)));
            return 1;
        }

        Console.Write(file.Content);
        return 0;
    }

    /// <summary>A codegen target of the project by name, else the first one with that generator, else the generator's defaults.</summary>
    private static CodegenTarget FindTarget(FormatProject project, string name)
    {
        var targets = project.Settings.Codegen;
        if ((targets.FirstOrDefault(t => t.Name == name) ?? targets.FirstOrDefault(t => t.Generator == name)) is { } target) return target;
        var generator = CodeGeneration.TryFind(name)
            ?? throw new UsageException($"No codegen target or generator '{name}' (targets: {string.Join(", ", targets.Select(t => t.Name).DefaultIfEmpty("none"))}; " +
                                        $"generators: {string.Join(", ", CodeGeneration.Generators.Select(g => g.Name))}).");
        return CodeGeneration.DefaultTarget(generator);
    }

    // ------------------------------------------------------------------ simulation

    public static int Decode(string[] args)
    {
        var line = CommandLine.Parse(args, ["project", "hex", "file"], ["tree", "json-only"]);
        if (line.Positional.Count != 1) throw new UsageException("Usage: format-studio decode <type> (--hex <hex> | --file <file>)");
        var project = LoadProject(line);
        var bytes = InputBytes(line);
        var result = new Codec(project).Decode(line.Positional[0], bytes);
        PrintDecode(result, bytes, line.Has("tree"), line.Has("json-only"));
        return result.Success ? 0 : 1;
    }

    public static int DecodePacket(string[] args)
    {
        var line = CommandLine.Parse(args, ["project", "hex", "file", "header", "id-mask", "group"], ["tree", "json-only"]);
        var project = LoadProject(line);
        var headerText = line.Single("header") ?? throw new UsageException("Pass --header <primitive>, e.g. varuint32.");
        if (!Primitives.TryParse(headerText, out var headerType)) throw new UsageException($"'{headerText}' is not a primitive type.");
        var mask = line.Single("id-mask") is { } maskText
            ? Convert.ToUInt64(maskText, maskText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10)
            : ulong.MaxValue;
        var bytes = InputBytes(line);
        var (packet, id, headerLength, result) = Simulator.DecodePacket(new Codec(project), bytes, new PacketHeader(headerType, mask), line.Single("group"));
        if (packet == null || result == null)
        {
            Console.Error.WriteLine($"No packet with id {id}.");
            return 1;
        }

        Console.WriteLine($"Packet {packet.Name} (id {id}, header {headerLength} bytes)");
        PrintDecode(result, bytes[headerLength..], line.Has("tree"), line.Has("json-only"));
        return result.Success ? 0 : 1;
    }

    private static void PrintDecode(DecodeResult result, byte[] bytes, bool tree, bool jsonOnly)
    {
        if (tree && !jsonOnly) PrintTree(result.Root, bytes, 0);
        Console.WriteLine(JsonValues.ToText(result.Value));
        if (!jsonOnly) Console.WriteLine($"Consumed {result.Consumed} of {bytes.Length} bytes.");
        PrintDiagnostics(result.Diagnostics);
    }

    private static void PrintTree(SpanNode node, byte[] bytes, int depth)
    {
        var length = Math.Max(0, Math.Min(node.Length, bytes.Length - node.Offset));
        var preview = Hex.Format(bytes.AsSpan(node.Offset, Math.Min(length, 12)));
        if (length > 12) preview += "..";
        var name = new string(' ', depth * 2) + node.Name;
        var text = $"{node.Offset,6:x4} {node.Length,5}  {name,-40} {node.Type,-24} {preview,-26} {node.Display}";
        if (node.Error != null) text += $"  <-- {node.Error}";
        Console.WriteLine(text.TrimEnd());
        foreach (var child in node.Children) PrintTree(child, bytes, depth + 1);
    }

    public static int Encode(string[] args)
    {
        var line = CommandLine.Parse(args, ["project", "json", "json-file", "seed", "out"], ["random", "tree"]);
        if (line.Positional.Count != 1) throw new UsageException("Usage: format-studio encode <type> (--json <json> | --json-file <file> | --random)");
        var project = LoadProject(line);
        var type = Codec.ParseType(line.Positional[0]);
        JsonNode? value;
        if (line.Has("random")) value = new RandomValues(project, line.Int("seed", Environment.TickCount)).Generate(type);
        else if (line.Single("json") is { } json) value = JsonNode.Parse(json);
        else if (line.Single("json-file") is { } file) value = JsonNode.Parse(File.ReadAllText(file));
        else throw new UsageException("Pass --json, --json-file or --random.");

        var codec = new Codec(project);
        var result = codec.Encode(type, value);
        if (line.Has("random")) Console.WriteLine(JsonValues.ToText(value));
        PrintDiagnostics(result.Diagnostics);
        if (!result.Success) return 1;
        if (line.Single("out") is { } outFile) File.WriteAllBytes(outFile, result.Bytes);
        if (line.Has("tree")) PrintTree(codec.Decode(type, result.Bytes).Root, result.Bytes, 0);
        Console.WriteLine(Hex.Format(result.Bytes));
        Console.WriteLine($"{result.Bytes.Length} bytes.");
        return 0;
    }

    public static int RoundTrip(string[] args)
    {
        var line = CommandLine.Parse(args, ["project", "iterations", "seed", "hex"], ["verbose"]);
        var project = LoadProject(line);
        if (line.Single("hex") is { } hex)
        {
            if (line.Positional.Count != 1) throw new UsageException("--hex needs exactly one type.");
            var result = Simulator.RoundTripBytes(new Codec(project), Codec.ParseType(line.Positional[0]), Hex.Parse(hex));
            PrintDiagnostics(result.Diagnostics);
            Console.WriteLine(result.Success ? "OK" : "FAILED");
            return result.Success ? 0 : 1;
        }

        IEnumerable<TypeDef> types = line.Positional.Count > 0
            ? line.Positional.Select(name => project.Find(name) ?? throw new UsageException($"Unknown type '{name}'."))
            : project.Packets.Any() ? project.Packets : project.Types.Values.OfType<StructDef>();
        var iterations = line.Int("iterations", 20);
        var failures = 0;
        var total = 0;
        foreach (var group in Simulator.Fuzz(project, types.OrderBy(t => t.Name, StringComparer.Ordinal), line.Int("seed", 1), iterations)
                     .GroupBy(r => r.Type))
        {
            var results = group.ToList();
            total += results.Count;
            var failed = results.Where(r => !r.Success).ToList();
            failures += failed.Count;
            if (failed.Count == 0)
            {
                if (line.Has("verbose")) Console.WriteLine($"ok      {group.Key}");
                continue;
            }

            Console.WriteLine($"FAILED  {group.Key}: {failed.Count} of {results.Count}");
            var first = failed[0];
            foreach (var diagnostic in first.Diagnostics.Where(d => d.Severity == Severity.Error).Take(3)) Console.WriteLine($"        {diagnostic}");
            if (line.Has("verbose"))
            {
                Console.WriteLine($"        value: {JsonValues.ToText(first.Value, false)}");
                Console.WriteLine($"        bytes: {Hex.Format(first.Bytes)}");
            }
        }

        Console.WriteLine($"{total - failures} of {total} round trips passed.");
        return failures == 0 ? 0 : 1;
    }

    public static int Identify(string[] args)
    {
        var line = CommandLine.Parse(args, ["project", "hex", "file", "top"], NoFlags);
        var project = LoadProject(line);
        var bytes = InputBytes(line);
        var matches = Simulator.Identify(new Codec(project), bytes);
        foreach (var match in matches.Take(line.Int("top", 10)))
        {
            var status = match.Result.Success ? match.Result.Consumed == bytes.Length ? "full" : "partial" : "error";
            Console.WriteLine($"{match.Score,6}  {status,-8} {match.Type.Name,-50} consumed {match.Result.Consumed}/{bytes.Length}, " +
                              $"{match.Result.Diagnostics.Count} diagnostics");
        }

        return 0;
    }

    public static int Samples(string[] args)
    {
        var line = CommandLine.Parse(args, ["project"], NoFlags);
        var project = LoadProject(line);
        var codec = new Codec(project);
        var failures = 0;
        foreach (var sample in project.Samples)
        {
            var type = Codec.ParseType(sample.Type);
            var problems = new List<string>();
            byte[]? bytes = null;
            if (sample.Value != null)
            {
                var encoded = codec.Encode(type, sample.Value);
                problems.AddRange(encoded.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()));
                bytes = encoded.Success ? encoded.Bytes : null;
                if (bytes != null && sample.Hex != null && !Hex.Parse(sample.Hex).AsSpan().SequenceEqual(bytes))
                    problems.Add($"encodes to {Hex.Format(bytes)}, expected {Hex.Format(Hex.Parse(sample.Hex))}");
            }

            bytes ??= sample.Hex != null ? Hex.Parse(sample.Hex) : null;
            if (bytes != null)
            {
                var roundTrip = Simulator.RoundTripBytes(codec, type, bytes);
                problems.AddRange(roundTrip.Diagnostics.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()));
            }

            if (problems.Count == 0)
            {
                Console.WriteLine($"ok      {sample.Name} ({sample.Type})");
                continue;
            }

            failures++;
            Console.WriteLine($"FAILED  {sample.Name} ({sample.Type}) in {sample.Source}");
            foreach (var problem in problems.Distinct()) Console.WriteLine($"        {problem}");
        }

        Console.WriteLine($"{project.Samples.Count - failures} of {project.Samples.Count} samples passed.");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ codegen

    public static int Generate(string[] args)
    {
        var line = CommandLine.Parse(args, ["project"], ["no-clean", "dry-run"]);
        var project = LoadProject(line);
        if (project.Diagnostics.HasErrors) throw new UsageException("Fix the errors above first (format-studio validate).");
        var targets = project.Settings.Codegen.Where(t => line.Positional.Count == 0 || line.Positional.Contains(t.Name)).ToList();
        if (targets.Count == 0) throw new UsageException("No codegen targets (add 'codegen:' to the project file).");

        var exit = 0;
        foreach (var target in targets)
        {
            var output = CodeGeneration.Generate(project, target);
            PrintDiagnostics(output.Diagnostics);
            if (output.Diagnostics.HasErrors) exit = 1;
            if (line.Has("dry-run"))
            {
                Console.WriteLine($"{target.Name}: {output.Files.Count} files (dry run).");
                continue;
            }

            var summary = CodeGeneration.Write(project, target, output, clean: !line.Has("no-clean"));
            Console.WriteLine($"{target.Name}: {output.Files.Count} files in {summary.OutputDirectory}: {summary.Written.Count} written, " +
                              $"{summary.Unchanged} unchanged, {summary.Deleted.Count} deleted.");
            foreach (var refused in summary.Refused)
            {
                Console.Error.WriteLine($"refused to overwrite {refused}: it has no generated-file marker");
                exit = 1;
            }
        }

        return exit;
    }

    public static int Generators(string[] args)
    {
        var line = CommandLine.Parse(args, [], ["verbose"]);
        line.MaxPositional(0, "format-studio generators [--verbose]");
        foreach (var generator in CodeGeneration.Generators)
        {
            Console.WriteLine($"{generator.Name,-16} {generator.Title} (default output: {generator.DefaultOutput})");
            Console.WriteLine($"{"",-16} {generator.Description}");
            foreach (var option in generator.Options.Where(o => line.Has("verbose") || !o.Advanced))
            {
                var defaultValue = option.Choices != null ? $" (one of {string.Join(", ", option.Choices)}; default: {option.Default})"
                    : option.Default != null ? $" (default: {option.Default.ToJsonString()})" : "";
                Console.WriteLine($"{"",-18}{option.Key,-24}{option.Description}{defaultValue}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Add a target to the project file:");
        Console.WriteLine("  codegen:\n    - name: java\n      generator: java-stream\n      output: generated/java\n      options: {package: com.example.protocol}");
        return 0;
    }

    public static int Web(string[] args)
    {
        var line = CommandLine.Parse(args, ["project", "workspace", "urls"], NoFlags);
        var options = new List<string>();
        if (line.Single("urls") is { } urls) options.Add($"--urls {urls}");
        if (line.Single("project") is { } project) options.Add($"--project \"{Path.GetFullPath(project)}\"");
        if (line.Single("workspace") is { } workspace) options.Add($"--workspace \"{Path.GetFullPath(workspace)}\"");
        Console.WriteLine("Start the web UI from the format-studio folder with:");
        Console.WriteLine("  dotnet run --project src/FormatStudio.Web" + (options.Count > 0 ? " -- " + string.Join(" ", options) : ""));
        Console.WriteLine("Without --project the start page lists the recent projects and can open or create one.");
        return 0;
    }
}
