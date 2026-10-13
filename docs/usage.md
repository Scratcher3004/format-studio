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
dotnet run --project src/FormatStudio.Web -- --urls http://localhost:5180 [--project <p>] [--workspace <folder>]
```

All options are optional. Without them the start page lists the recent projects and can open any project (path or
folder browser) or create a new one from the same template as `format-studio new`.

| Option | |
|---|---|
| `--project <file\|folder>` | Opens the project at startup; `/` goes straight to it. Also `FORMATSTUDIO_PROJECT`. |
| `--workspace <folder>` | Lists every `*.fstudio.yaml` below the folder on the start page (skips hidden folders, `bin`, `obj`, `node_modules`, `generated`). |
| `--state-dir <folder>` | Where the recent projects are kept; default `~/.config/format-studio` (`%APPDATA%\format-studio` on Windows). |

Relative paths are relative to the folder `dotnet run` is started from.

Every project page has the project in its URL (`/p/<id>/type/Foo`), so pages can be bookmarked and several projects
can be open in different tabs. The id is the file name plus a hash of the path; it stays the same across restarts as
long as the project is in the recent list or the workspace. The project name in the app bar switches projects.

Type browser with filter; per type: structure table, simulator (JSON ⇄ hex with an annotated hex view and value
tree, random values, round trip), YAML editor with validation (imported files are saved as overrides), generated C#,
references. Also pages for identify, round-trip fuzzing over all types, code generation (preview and write),
imports (dry run and report) and new definitions.
