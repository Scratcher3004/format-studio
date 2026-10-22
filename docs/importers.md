# Importers

An importer converts an external description of a format into DSL files. It is configured in the project file under
`import.<name>` and run with `format-studio import <name>` or on the web UI's Import page; options it declares can be
overridden for one run (`--schema ...` on the command line, text fields in the web UI). Each file it writes starts with
the importer's marker; only such files are replaced or deleted by the next import, so corrections belong in an
override folder listed after the import folder in `sources`.

Importers implement `FormatStudio.Importers.IImporter` (name, title, marker, options, `Run(project, settings, write)`)
and are registered at startup with `ImporterRegistry.Register` (see `src/FormatStudio.Cli/Program.cs`).

```yaml
import:
  mojang:
    schema: ../downloads/bedrock-protocol-docs-v1.26.51.zip
    output: imported
```

## Mojang importer (`mojang`)

In `src/FormatStudio.Importers.Mojang`. Without a project, its own validation assumes `bedrock-network` NBT; the
project should set `defaults.nbt: bedrock-network` and declare `dependencies: [minecraft]` for the NBT type.

`import mojang` converts a release of [Mojang/bedrock-protocol-docs](https://github.com/Mojang/bedrock-protocol-docs)
(zip or folder) into DSL files under `output` (`packets/`, `types/`, `enums/`, `variants/`, `aliases/`) plus
`import-report.md`. Each file starts with a generated header; only such files are replaced or deleted by the next
import. Corrections go into an override folder listed after the import folder in `sources`.

Mapping:

- Packets: `$metaProperties["[cereal:packet]"]` is the id; the `$ref` payload is inlined.
- Fields in `x-ordinal-index` order; names in PascalCase (the original name is kept as `title`).
- `x-underlying-type` + `x-serialization-options`: `Compression` gives varuint/zigzag, `Big Endian` gives `be`,
  `No size compression` gives a `u32` count; arrays without options get the default `varuint32` count; arrays of
  `uint8` become `bytes`; arrays with equal `minItems`/`maxItems` have a fixed count.
- Not required and no `default`: optional (presence byte).
- Enum references: the use site's encoding becomes `as` when it differs from the enum's underlying type.
- `oneOf` becomes a variant. When every case is a reference to an object whose first field is a `const` of the same
  enum, that field is the tag (e.g. ResourcePackClientResponse, DataItem); otherwise the tag is a `varuint32` case
  index (`variantTag`). Both are listed in the report.
- Properties without a type become `nbt` (network NBT compound tags such as `__[[CTD]]__`).
- `additionalProperties` becomes a map; non-packet structs with one field become aliases (`inlineSingleFieldStructs`).
- **Enum values:** releases up to v1.26.51 have no `x-enum-binary-value`. Values are taken from the release, then from
  `enumValuesFrom` snapshots (e.g. the `main` branch), then from the previous import, and only then from the member
  index, which the report and the enum's doc flag as unverified.
