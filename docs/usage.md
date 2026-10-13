# Command line and web UI

## Command line

```
dotnet run --project src/FormatStudio.Cli -- <command> --project <file|folder> ...
```

| Command | |
|---|---|
| `new <folder>` | Create an empty project |
| `validate` | Check references, expressions, tags, ids |
| `list [--kind packet] [--filter text]` | List definitions |
| `show <type> [--csharp]` | Print the definition or its generated C# |
| `decode <type> --hex ... [--tree]` | Decode bytes; `--tree` prints every value with its byte range |
| `decode-packet --hex ... --header varuint32 --id-mask 0x3ff` | Read a packet id, then the packet |
| `encode <type> --json ... \| --random --seed 1` | Encode a value (or a random one) |
| `roundtrip [types] --iterations 20` | Random values: encode, decode, encode again, compare |
| `identify --hex ...` | Try every packet (or struct) and rank the matches |
| `samples` | Check the project's samples |
| `import <importer> [options] [--dry-run]` | Run an importer (see [importers](importers.md)) |
| `generate [target] [--dry-run]` | Run the codegen targets |

## Web UI

```
dotnet run --project src/FormatStudio.Web -- --project <absolute path of the project> --urls http://localhost:5180
```

`dotnet run` starts the web UI in its project folder, so give the project as an absolute path.

Type browser with filter; per type: structure table, simulator (JSON ⇄ hex with an annotated hex view and value
tree, random values, round trip), YAML editor with validation (imported files are saved as overrides), generated C#,
references. Also pages for identify, round-trip fuzzing over all types, code generation (preview and write),
imports (dry run and report) and new definitions.
