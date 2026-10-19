# Command line and web UI

## Command line

```
dotnet run --project src/FormatStudio.Cli -- <command> --project <file|folder> ...
```

| Command | |
|---|---|
| `new <folder> [--library [--version 1.0.0] [--inherit endian,length]]` | Create an empty project or library |
| `validate` | Check references, expressions, tags, ids |
| `list [--kind packet] [--filter text] [--libraries]` | List definitions (`--libraries`: also those of the libraries) |
| `pack [--output <folder>]` | Pack a library project into `<name>-<version>.fslib` (see [libraries](libraries.md)) |
| `libraries` | List the libraries the project loads |
| `natives` | List the native types the project can use: built-in, its own and its libraries' (see [native types](natives.md)) |
| `show <type> [--target <target\|generator>]` | Print the definition, or its generated code (`--csharp` is `--target csharp`) |
| `decode <type> --hex ... [--tree]` | Decode bytes; `--tree` prints every value with its byte range |
| `decode-packet --hex ... --header varuint32 --id-mask 0x3ff [--protocol <name>]` | Read a packet id, then the packet (of that protocol) |
| `encode <type> --json ... \| --random --seed 1` | Encode a value (or a random one) |
| `roundtrip [types] --iterations 20` | Random values: encode, decode, encode again, compare |
| `identify --hex ... [--protocol <name>]` | Try every packet (or struct, or the protocol's packets) and rank the matches |
| `test [--filter ...] [--tag ...] [--format text\|json\|junit] [--update] [--target ...]` | Run the project's test cases (see [testing](testing.md)); `--update` writes what failed cases give into their files; `--target` runs them against generated C# or Java too; `samples` is an alias |
| `import <importer> [options] [--dry-run]` | Run an importer (see [importers](importers.md)) |
| `generate [target] [--dry-run]` | Run the codegen targets (see [code generation](codegen.md)) |
| `generators [--verbose]` | List the code generators and their options: built-in, and those the project and its libraries declare |

## Web UI

```
dotnet run --project src/FormatStudio.Web -- --urls http://localhost:5180 [--project <p>] [--workspace <folder>]
```

All options are optional. Without them the start page lists the recent projects and can open any project (path or
folder browser) or create a new one from the same template as `format-studio new`.

| Option | |
|---|---|
| `--project <file\|folder>` | Opens the project at startup; `/` goes straight to it. Also `FORMATSTUDIO_PROJECT`. |
| `--workspace <folder>` | Lists every `*.fstudio.yaml` below the folder on the start page (skips hidden folders, `bin`, `obj`, `node_modules`, `generated`). The web UI then reads, writes and lists nothing outside the folder, also not through symbolic links or junctions or through project settings like `sources: [../**/*.yaml]`; `--project` must be inside it. |
| `--state-dir <folder>` | Where the recent projects are kept; default `~/.config/format-studio` (`%APPDATA%\format-studio` on Windows). |
| `--plugins all\|none\|<sha256>,...` | Which plugins ([native types](natives.md#which-plugins-are-loaded), code generators) are loaded; default `all`. Also `FORMATSTUDIO_PLUGINS`. |

Relative paths are relative to the folder `dotnet run` is started from.

Every project page has the project in its URL (`/p/<id>/type/Foo`), so pages can be bookmarked and several projects
can be open in different tabs. The id is the file name plus a hash of the path; it stays the same across restarts as
long as the project is in the recent list or the workspace. The project name in the app bar switches projects.

Type browser with filter; per type: structure table, simulator (JSON ⇄ hex with an annotated hex view and value
tree, random values, round trip), editor (imported files are saved as overrides), generated code for any target or
language, references. Also pages for identify, round-trip fuzzing over all types, code generation (add, edit and
remove targets, preview and write), imports (dry run and report), new definitions and the project's settings.

The start page's New form creates projects and libraries (kind, version and the defaults a library takes from the
projects using it). **Settings** edits the project file: name, kind, version, description, defaults (those a library
inherits are greyed out), dependencies (picked from the packages in the library folders), sources, tests and
library folders. Only those sections are rewritten; imports, protocols and codegen targets and the comments between
sections stay. In a library, new definitions start from a struct and packets can't be picked; the Packet switch
appears only when editing a struct, and is greyed out in libraries.

The editor of new and existing definitions has two views of the same YAML, switched with Builder / YAML:

- **Builder** (the default): forms for every kind. Packets and structs get a field list (add, duplicate, reorder,
  remove) where each field opens to a type builder (primitives with byte order, references with `as`/`tag`,
  strings, bytes, arrays (also ending on a condition, `until`), optionals, maps, sized values, fixed point, NBT, all with their lengths, or a type typed
  as text) and the field options (`const`, `computed`, `if`, `default`, `min`/`max`, `minLength`/`maxLength`,
  `pattern`, `minItems`/`maxItems`, `title`, `doc`). Packet ids, enum values, numeric variant tags and integer
  `const`/`default`/`min`/`max` have a dec/hex/bin toggle at the end of the input; typing `0x..` or `0b..` switches
  as well. The choice is stored in the YAML as the form of the number. Changes in the builder rewrite the YAML, which
  drops comments.
- **YAML**: the file as text. YAML with errors can't be opened in the builder until they are fixed.

Validation runs on every change. Next to the editor, **sample data** (pasted hex or an uploaded file, at most
64 KiB, optionally skipping a header) is decoded with the definition as it is being edited, with the annotated hex
view, value tree and JSON, so a packet can be built field by field against a capture.

The [test cases](testing.md) of a project are listed on the overview with their suite and tags (long hex is cut; hover
for all of it), where they can be run, deleted or opened; after a run, **Accept actual** writes what a failed case
gives into its file (**Accept all actual** for all of them, as `test --update` does). A case's page edits its name, type, file, value and hex, and
decodes the hex (or, without hex, encodes the value) into the annotated hex view and value tree, so a failing case can
be debugged byte by byte; Encode, Decode and Round trip work as on the Simulate page, and their results are what Save
writes. Its other keys (a bytes file, expectations, a packet header, fuzzing, skip, tags) are listed and kept; a packet
case's value and hex are plain text fields, since its bytes start with the packet id. New cases go to a file matched by
the project's `tests` patterns (by default the first file with cases). Saving or deleting rewrites the test file,
keeping its shape and the other cases but not comments.
Test files are checked when the project loads: an unknown type, key or protocol, invalid hex, a missing bytes file, a
case with nothing to test, a packet case whose type isn't one of the packets it may be, or a repeated name shows up with the other diagnostics, at the case's line.
