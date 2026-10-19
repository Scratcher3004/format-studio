# Libraries

A library is a project whose definitions other projects use: shared types such as positions, item stacks or
text components, written once and packed into a versioned `.fslib` package.

## Library projects

A library is an ordinary project with `kind: library`. Its name must be an identifier (it qualifies its definitions,
`geo.Point`), it needs a `version` to be packed, and it can't have packets or protocols (yet). `format-studio new <folder>
--library [--version <version>] [--inherit endian,length,string]` and the web UI's New form (kind Library) create one;
the web UI's Settings page turns a project into a library and back, and edits its version, `inherit` and dependencies.

```yaml
name: geo
kind: library
version: "1.2.0"
defaults:
  endian: little
  length: u8
  inherit: [endian]   # optional: take these defaults from the project using the library (or "inherit: true" for all)
dependencies:         # libraries may use other libraries
  - library: units
```

`format-studio pack --output <folder>` checks the library and writes `<name>-<version>.fslib`: a zip with the project
file, the files of its definitions, its [native types](natives.md) with their plugins and runtime files and its
[code generators](generators.md#using-a-generator-in-a-project) with their plugins and its
[code for native types](natives.md#code-for-other-generators) (samples and
codegen settings stay out). Packages are meant to be committed or
copied into the library folders of the projects that use them.

## Using libraries

```yaml
dependencies:
  - library: geo
    version: "1.2.0"        # optional: the exact version; without, the highest one found
    as: g                   # optional: the name the project uses for it (default: the library's name)
    codegen:                # optional, see "Code generation"
      skip: true
      namespace: Geo.Types
      package: geo.types
libraries: [libraries, ../shared/libraries]   # folders with .fslib packages; default: [libraries]
```

Packages are found in the `libraries` folders (relative to the project, inside the web UI's workspace), then among
the built-in libraries that come with Format Studio. `format-studio libraries` lists what a project loads, with
versions and packages.

- **Names.** A library's definitions are referenced as `g.Point` (by alias) or `geo.Point` (by library name), and by
  name alone (`Point`) when the project has no definition of that name and no other direct dependency has one;
  otherwise the name is reported as ambiguous. Enum constants work the same way: `geo.Shape::circle`.
- **Dependencies of libraries** are loaded too, but only reachable by their qualified name (`units.Meter`), not by
  name alone. Each library is loaded once per project; two different versions of one library are an error, as are
  libraries that depend on each other.
- **Defaults.** A library's definitions keep the library's defaults (endian, length prefix, string encoding, the
  arguments of native types such as the NBT flavor), whatever the project's are. Settings listed in the library's
  `inherit` (`endian`, `length`, `string` or a native type's name such as `nbt`) come from the project instead. Type
  arguments of native types (`zlib(Chunk)`) are types of the library like any other.
- **Read-only.** Library definitions are checked when loaded (their errors are reported with the library's name) and
  can't be edited in the project. The web UI lists them per library in the sidebar and shows them as the project
  sees them: names qualified, defaults written out (`show geo.Point` on the command line).

## Code generation

By default the generated code of a project includes the library definitions it uses (directly or through other
library definitions), each library in a namespace or package of its own inside the target's: `My.Protocol.Geo`
(C#) or `my.protocol.geo` (Java), in a folder of that name. Library definitions the project doesn't use are left out.

A dependency's `codegen` changes that:

| Setting | |
|---|---|
| `skip: true` | Nothing is generated for the library; its code comes from elsewhere (e.g. a package built from the library). |
| `namespace` | The C# namespace of the library's types, generated or (with `skip`) referenced. |
| `package` | The Java package of the library's types. |

Settings only apply to direct dependencies; list a library's own dependency as a dependency of the project to
configure it.
