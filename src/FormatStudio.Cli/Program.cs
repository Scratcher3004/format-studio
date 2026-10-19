using FormatStudio.Cli;
using FormatStudio.Importers;
using FormatStudio.Importers.Mojang;
using FormatStudio.Model;

const string usage = """
    format-studio - define, analyse and simulate binary data formats, generate code

    Projects are folders with a <name>.fstudio.yaml file and one YAML file per type. Commands that need a project
    take --project <file|folder> (default: the nearest *.fstudio.yaml in the current folder or above).
    FORMATSTUDIO_PLUGINS=all|none|<sha256>,... limits the plugins of native types that are loaded (default: all).

    Usage:
      format-studio new <folder> [--name <name>] [--endian little|big] [--length <prefix>]
                  [--library [--version <version>] [--inherit endian,length,string]]
          Creates an empty project, or a library (version default 0.1.0) whose --inherit defaults come from the
          projects using it.
      format-studio import <importer> [<importer options>] [--dry-run]
          Runs an importer with the project's settings (import.<importer>). Only files with the importer's
          generated header are replaced. Importers:
            mojang [--schema <zip|folder>] [--enum-values-from <zip|folder>]... [--output <folder>]
                Mojang/bedrock-protocol-docs: one file per type and import-report.md.
      format-studio validate
          Checks all definitions; exit code 1 on errors.
      format-studio pack [--output <folder>]
          Packs a library project (kind: library) into <name>-<version>.fslib, for other projects' library folders.
      format-studio libraries
          Lists the libraries the project loads (dependencies of libraries included), their versions and packages.
      format-studio natives
          Lists the native types the project can use: built-in, declared by the project and by its libraries, and
          the code for other generators that native-codegen files add to them.
      format-studio list [--kind packet|struct|enum|variant|alias|bitfield] [--filter <text>] [--libraries]
          Lists the definitions; --libraries adds those of the libraries (as Library.Name).
      format-studio show <type> [--target <target|generator>] [--csharp]
          Prints a definition (DSL), or its generated code for a codegen target of the project or a generator's
          defaults (--csharp is --target csharp).
      format-studio decode <type> (--hex <hex> | --file <file>) [--tree] [--json-only]
          Decodes bytes as a type. --tree prints the annotated byte ranges.
      format-studio decode-packet (--hex <hex> | --file <file>) --header <primitive> [--id-mask <mask>] [--protocol <name>]
                  [--group <group>] [--tree]
          Reads a packet id first, then decodes the packet with that id (of the protocol or group, if given).
      format-studio encode <type> (--json <json> | --json-file <file> | --random [--seed <n>]) [--out <file>] [--tree]
          Encodes a JSON value (or a random one) and prints the bytes as hex.
      format-studio roundtrip [<type>...] [--iterations <n>] [--seed <n>] [--hex <hex>] [--verbose]
          Encodes random values (or decodes --hex), re-encodes and compares. Without types: all packets/structs.
      format-studio identify (--hex <hex> | --file <file>) [--top <n>] [--protocol <name>] [--group <group>]
          Tries the bytes against every packet (or struct, or those of the protocol or group) and ranks the matches.
      format-studio test [--filter <glob>] [--tag <tag>] [--type <type>] [--file <glob>] [--format text|json|junit]
                  [--output <file>] [--update]
          Runs the project's test cases against the simulator (see docs/testing.md). Filters may be repeated; --filter
          matches name or suite/name, --file the test file. --output writes the report to a file (and a text summary
          to the console). --update first writes what failed cases give (hex, expected value, warnings, length) into
          their files, and fills in 'auto'. Exit code 1 when a case fails or the project has errors. 'samples' is an
          alias.
      format-studio generate [<target>...] [--no-clean] [--dry-run]
          Runs the project's codegen targets. Output only goes to the target's output folder.
      format-studio generators [--verbose]
          Lists the code generators and their options: the built-in ones (csharp, csharp-stream, java-stream) and,
          in a project, those it and its libraries declare.
      format-studio web [--project <p>] [--workspace <folder>] [--urls <url>]
          Hint for starting the web UI (FormatStudio.Web).
    """;

ImporterRegistry.Register(new MojangImport());

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(usage);
    return args.Length == 0 ? 1 : 0;
}

try
{
    return args[0] switch
    {
        "new" => Commands.New(args[1..]),
        "import" => Commands.Import(args[1..]),
        "validate" => Commands.Validate(args[1..]),
        "pack" => Commands.Pack(args[1..]),
        "libraries" => Commands.Libraries(args[1..]),
        "natives" => Commands.Natives(args[1..]),
        "list" => Commands.List(args[1..]),
        "show" => Commands.Show(args[1..]),
        "decode" => Commands.Decode(args[1..]),
        "decode-packet" => Commands.DecodePacket(args[1..]),
        "encode" => Commands.Encode(args[1..]),
        "roundtrip" => Commands.RoundTrip(args[1..]),
        "identify" => Commands.Identify(args[1..]),
        "test" or "samples" => Commands.Test(args[1..]),
        "generate" => Commands.Generate(args[1..]),
        "generators" => Commands.Generators(args[1..]),
        "web" => Commands.Web(args[1..]),
        _ => throw new UsageException($"Unknown command '{args[0]}'."),
    };
}
catch (UsageException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    Console.Error.WriteLine("Run 'format-studio --help' for usage.");
    return 2;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or FormatDefinitionException or System.FormatException
                                      or UnauthorizedAccessException or System.Text.Json.JsonException)
{
    var location = exception is FormatDefinitionException { Location: { } source } ? $"{source}: " : "";
    Console.Error.WriteLine($"error: {location}{exception.Message}");
    return 1;
}
