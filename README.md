# Format Studio

A tool for describing, analysing and simulating binary data formats, and for generating code from the descriptions.

Formats are written in a small YAML DSL, one file per type, grouped into projects. Format Studio validates them,
encodes and decodes arbitrary values with an annotated byte view, fuzzes round trips, guesses which packet some bytes
are, checks samples with known encodings, imports external schemas and generates C#. It handles little- and big-endian
formats, LEB128 and zigzag varints, length prefixes of any width, conditional and computed fields, tagged unions with
inline or external tags, bit fields, fixed-point numbers and NBT. Very custom bit-level formats are out of scope.

```yaml
packet: Particle
id: 0x27
fields:
  - particleId: varint32
  - name: data
    type: ParticleData
    tag: particleId
  - name: count
    type: varint32
    computed: len(matches)
  - name: matches
    type: CommandMatch[count]
```

## Quick start

Requires the .NET 10 SDK.

```
dotnet run --project src/FormatStudio.Cli -- new my-format --endian big
dotnet run --project src/FormatStudio.Cli -- roundtrip --project samples/java-protocol
dotnet run --project src/FormatStudio.Web -- --workspace samples --urls http://localhost:5180
```

## Repository

| Folder | |
|---|---|
| `src/FormatStudio.Core` | Model, YAML DSL, project loader, validator, simulator, NBT, code generation |
| `src/FormatStudio.Cli` | `format-studio` command-line tool |
| `src/FormatStudio.Web` | Web UI (Blazor Server, MudBlazor) |
| `src/FormatStudio.Importers.Mojang` | Importer for [Mojang/bedrock-protocol-docs](https://github.com/Mojang/bedrock-protocol-docs) |
| `src/FormatStudio.Runtime` | Runtime for the generated C# |
| `src/FormatStudio.Testing` | Checks generated C# against the simulator (for your own tests) |
| `tests/FormatStudio.Tests` | Tests |
| `samples/java-protocol` | Part of the Minecraft Java protocol as a sample project |

## Documentation

- [Projects and DSL](docs/dsl.md): project files, definitions, types, expressions, JSON values
- [Command line and web UI](docs/usage.md)
- [Projects in the web UI](docs/web-projects.md): start page, project URLs, planned hosted mode
- [Importers](docs/importers.md)
- [C# generator](docs/csharp.md): generated code, runtime, checking it against the simulator

## Tests

`dotnet test FormatStudio.slnx`: DSL round trips and errors, expressions, the codec (varints, byte orders, variants,
bit fields, sized sections, NBT in all four flavors), the importer on a synthetic schema, the sample project's samples
and random round trips, and the generated C# compiled with Roslyn and checked byte for byte against the simulator.

## License

MIT, see [LICENSE](LICENSE).
