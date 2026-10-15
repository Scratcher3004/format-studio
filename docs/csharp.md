# C# generators

Two generators produce the same types and differ in what they read from and write to:

| Generator | Reader / writer | Use for |
|---|---|---|
| `csharp` | `ref struct FormatReader` over `ReadOnlySpan<byte>`, `ref struct FormatWriter` over `IBufferWriter<byte>` | Whole messages in memory; no allocations besides the values |
| `csharp-stream` | `FormatStreamReader` / `FormatStreamWriter` classes over a `System.IO.Stream` | Network and file streams, without buffering whole messages |

```yaml
codegen:
  - name: csharp
    generator: csharp            # or csharp-stream
    output: generated/csharp     # inside the project folder
    options:
      namespace: My.Protocol
      registry: Packets
```

Options: `namespace`, `registry` (static class with the packet id dispatch), `validateRanges`, `validateEnums` (reject
values that are not members, except for flags enums and enums with `allowUnknown`), and the runtime names below.
Definitions written by hand are skipped with `skipCodegen` in the definition (see
[code generation](codegen.md#skipping-definitions), which also describes what all generators share).

- Structs become `partial struct`s with public fields and `static Read(reader)` / `Write(writer)` (by `ref` for
  spans); types that contain themselves by value become `sealed partial class`es.
- Packets implement the packet interface with `static uint PacketId`; `<registry>.Dispatch(id, reader, ref visitor)`
  switches over the ids without boxing.
- Variants are tagged structs: `Kind` (the tag enum, or a nested `Case` enum), one field per case,
  `FromCase(...)` factories, `Read(reader, kind)` / `WriteContent` for external tags. With string tags the `Case`
  values are the case indexes, and `CaseOf(tag)` / `TagOf(kind)` convert.
- Arrays with `until` are read element by element until the condition holds for the last one read.
- Aliases are inlined, computed and const fields are not exposed, conditional fields are nullable.
- Counts are checked against the remaining bytes and `maxItems` before allocating. A `FormatStreamReader` over a
  stream of unknown length (not seekable, or `length: -1`) checks counts against `MaxCount` (16M) instead, and `eof`
  lengths read until the stream ends.
- NBT is kept as raw bytes (`byte[]`).

## Runtime

The generated code calls a small runtime, `FormatStudio.Runtime` (no dependencies; reference the project or copy its
files):

| Option | `csharp` | `csharp-stream` | |
|---|---|---|---|
| `runtimeNamespace` | `FormatStudio.Runtime` | `FormatStudio.Runtime` | |
| `reader` | `FormatReader` | `FormatStreamReader` | |
| `writer` | `FormatWriter` | `FormatStreamWriter` | |
| `serializableInterface` | `IFormatSerializable` | `IFormatStreamSerializable` | `static abstract T Read(reader)`, `void Write(writer)` |
| `packetInterface` | `IFormatPacket` | `IFormatStreamPacket` | adds `static abstract uint PacketId` |
| `visitorInterface` | `IFormatPacketVisitor` | `IFormatStreamPacketVisitor` | `void Visit<T>(ref T packet) where T : packet interface` |
| `exception` | `FormatDataException` | `FormatDataException` | thrown for malformed data |

To use your own runtime, set these options to its type names. It needs the same methods as the default reader and
writer: `Read<Kind>()` / `Write<Kind>(value)` for every primitive (`U8`, `I16LE`, `U32BE`, `VarUInt32`, `ZigZag64`,
`UuidLE`, `F64BE`, ...), `ReadUtf8(count)`, `ReadUtf8Zero()`, `ReadBytes(count)`, `Slice(length)`,
`CheckCount(count, minElementSize, max)`, `Utf8Length(text)`, and `ReadNbt<Flavor>()` / `WriteNbt<Flavor>(raw)` for
the NBT flavors the project uses (`Java`, `JavaNetwork`, `Bedrock`, `BedrockNetwork`). For `eof` lengths, span readers
need `Remaining`; stream readers need `AtEnd`, `ReadToEnd()` and `ReadUtf8ToEnd()`.

## Checking generated code

See [code generation](codegen.md#checking-generated-code): `GeneratedCodeVerifier.CheckAgainstSimulator` compiles a
`csharp` or `csharp-stream` target with Roslyn and compares it byte for byte with the simulator.

## Not supported

- Everything that [no generator supports](codegen.md#not-supported-by-any-generator).
- Packet ids outside the `uint` range (negative ids).
- `csharp` (spans): reading from a stream; the whole message must be in memory. Use `csharp-stream` for streams.
- `csharp-stream`: reading without allocating; values are read through a small buffer, and `sized` values are
  written to a temporary buffer to learn their length.
