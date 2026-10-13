# C# generator

```yaml
codegen:
  - name: csharp
    generator: csharp
    output: generated/csharp     # inside the project folder
    options:
      namespace: My.Protocol
      registry: Packets
```

Options: `namespace`, `registry` (static class with the packet id dispatch), `exclude` (types written by hand: they
are referenced but not generated), `validateRanges`, `validateEnums`, and the runtime names below.

- Structs become `partial struct`s with public fields and `static Read(ref reader)` / `Write(ref writer)`; types that
  contain themselves by value become `sealed partial class`es.
- Packets implement the packet interface with `static uint PacketId`; `<registry>.Dispatch(id, ref reader, ref visitor)`
  switches over the ids without boxing.
- Variants are tagged structs: `Kind` (the tag enum, or a nested `Case` enum), one field per case,
  `FromCase(...)` factories, `Read(ref reader, kind)` / `WriteContent` for external tags.
- Aliases are inlined, computed and const fields are not exposed, conditional fields are nullable.
- Counts are checked against the remaining bytes and `maxItems` before allocating.
- NBT is kept as raw bytes (`byte[]`).

The output only goes to the target's folder inside the project, and only files starting with the generated-file
marker are replaced or deleted (others are reported and left alone). Copying the reviewed output into a source tree is
deliberately a manual step.

## Runtime

The generated code calls a small runtime. The `default` profile uses `FormatStudio.Runtime` (no dependencies; reference
the project or copy its three files):

| Option | Default | |
|---|---|---|
| `runtimeNamespace` | `FormatStudio.Runtime` | |
| `reader` | `FormatReader` | `ref struct` over a `ReadOnlySpan<byte>` |
| `writer` | `FormatWriter` | `ref struct` over an `IBufferWriter<byte>` |
| `serializableInterface` | `IFormatSerializable` | `static abstract T Read(ref reader)`, `void Write(ref writer)` |
| `packetInterface` | `IFormatPacket` | adds `static abstract uint PacketId` |
| `visitorInterface` | `IFormatPacketVisitor` | `void Visit<T>(ref T packet) where T : packet interface` |
| `exception` | `FormatDataException` | thrown for malformed data |

To use your own runtime, set these options to its type names. It needs the same methods as `FormatReader` and
`FormatWriter`: `Read<Kind>()` / `Write<Kind>(value)` for every primitive (`U8`, `I16LE`, `U32BE`, `VarUInt32`,
`ZigZag64`, `UuidLE`, `F64BE`, ...), `ReadUtf8(count)`, `ReadUtf8Zero()`, `ReadBytes(count)`, `Slice(length)`,
`CheckCount(count, minElementSize, max)`, `Remaining`, `Utf8Length(text)`, and `ReadNbt<Flavor>()` /
`WriteNbt<Flavor>(raw)` for the NBT flavors the project uses (`Java`, `JavaNetwork`, `Bedrock`, `BedrockNetwork`).

## Checking generated code

`FormatStudio.Testing` compiles a target's output with Roslyn against a runtime assembly and checks it against the
simulator: random values encoded by the simulator must be read and written back byte for byte by the generated code.

```csharp
var project = FormatProject.Load("path/to/project");
var target = project.Settings.Codegen.Single(t => t.Name == "csharp");
var failures = GeneratedCodeVerifier.CheckAgainstSimulator(project, target, typeof(MyReader).Assembly, iterations: 5);
Assert.Empty(failures);
```
