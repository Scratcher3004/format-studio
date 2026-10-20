# Java generator

`java-stream` generates Java 8+ classes that read from an `InputStream` and write to an `OutputStream`.

```yaml
codegen:
  - name: java
    generator: java-stream
    output: generated/java       # inside the project folder
    options:
      package: com.example.protocol
```

| Option | Default | |
|---|---|---|
| `package` | `generated` | Package of the generated classes |
| `registry` | `Packets` | Class with the packet id dispatch |
| `unsigned` | `widen` | How unsigned integers are represented, see below |
| `collections` | `list` | `list`: arrays are `java.util.List`s; `array`: Java arrays (`int[]`, `Item[]`) |
| `validateRanges` | `false` | Check `min`/`max` of numeric fields when reading |
| `validateStrings` | `true` | Reject invalid UTF-8 or ASCII, and padding or fill with `check: strict` |
| `includeRuntime` | `true` | Write the runtime's sources into the output |
| `runtimePackage` | `formatstudio.runtime` | Package of the runtime the generated code uses |

Definitions that are written by hand are skipped with `skipCodegen` in the definition, see
[code generation](codegen.md#skipping-definitions), which also describes what all generators share.

```java
FormatReader reader = new FormatReader(socket.getInputStream());
Handshake handshake = Handshake.read(reader);

ByteArrayOutputStream buffer = new ByteArrayOutputStream();
handshake.write(new FormatWriter(buffer));
```

- Structs and packets become `final class`es with public fields, `static read(FormatReader)` and
  `write(FormatWriter)`. Packets implement `FormatPacket` (`ID`, `packetId()`).
- Signed integers use the Java type of their size (`i8` → `byte`, `i16` → `short`, `i32`/`varint32`/`zigzag32` →
  `int`, 64-bit → `long`). `f16`/`f32` are `float`, `f64` is `double`, `uuid` is `java.util.UUID`, strings are `String`, bytes
  and NBT are `byte[]`.
- Arrays are `java.util.List`s or, with `collections: array`, Java arrays. Maps are always lists of `Map.Entry`
  (keeping order and duplicates). Optional and conditional values use the boxed types (`Integer`, ...) and `null`
  when absent.
- Enums become Java `enum`s with the wire `value` and `fromValue(...)`, which throws for unknown values. Flags enums
  and enums with `allowUnknown` become classes of constants and their values are plain numbers. Both use the Java
  type of the enum's `type` (a `u8` enum is `int` with `widen`, `byte` otherwise; a `zigzag32` enum is `int`), also
  when the wire type is changed with `as`. The same goes for the `value` of a variant's `Case` enum.
- Variants are classes with `kind` (the tag enum, or a nested `Case` enum with `value` or `tag`), one field per case
  and `ofCase(...)` factories; `read(reader, kind)` and `writeContent` are for external tags.
- The registry has `read(id, reader)` (returns the packet, or `null` for unknown ids), `dispatch(id, reader, visitor)`,
  `nameOf(id)` and `ids()`. Packet ids must fit an `int`. With [protocols](dsl.md#protocols) each protocol is a
  nested class with these methods: `Packets.LoginServerbound.read(id, reader)`.
- Names that are Java keywords get a `_` suffix; fields named `ID` or `kind` (and enum members named `value` or `tag`)
  are renamed so they don't clash with the generated members. A variant named `Case` with a nested `Case` enum is the
  class `CaseType` (with a warning), and other definitions named `Case` are written with their package.

## Unsigned integers

Java has no unsigned integer types. The option `unsigned` chooses how `u8`, `u16`, `u32`, `varuint32`, `u64` and
`varuint64` are represented:

| `unsigned` | `u8` | `u16` | `u32`, `varuint32` | `u64`, `varuint64` | |
|---|---|---|---|---|---|
| `widen` | `int` | `int` | `long` | `long` | Values stay positive. 64-bit values have no wider type: they keep their bits, and every such field gets a warning |
| `signed` | `byte` | `short` | `int` | `long` | Same size, same bits: values above the signed range are negative (`Byte.toUnsignedInt`, `Integer.toUnsignedLong`, `Long.compareUnsigned` convert) |
| `checked` | `byte` | `short` | `int` | `long` | Like `signed`, but values above the signed range are rejected with a `FormatDataException` when reading and when writing |

Expressions (conditions, computed fields, lengths) always use the unsigned value. With `checked`, enum members and
variant tags above the signed range are an error, and computed values (e.g. a `u8` count of 200 elements) that don't
fit are rejected when writing.

## Runtime

With `includeRuntime` the runtime's sources (`FormatReader`, `FormatWriter`, `FormatSerializable`, `FormatPacket`,
`FormatPacketVisitor`, `FormatDataException`) are written into the output, in `runtimePackage`, so the output compiles
on its own. Turn it off when several targets share one copy of the runtime. The sources are in
`src/FormatStudio.Runtime.Java`.

`FormatReader` reads from a stream of unknown length (counts are then checked against `maxCount()`, 16M by default,
and `eof` lengths read until the stream ends) or from a byte array or a stream with a known length (counts are checked
against the remaining bytes). Wrap unbuffered streams in `BufferedInputStream` / `BufferedOutputStream`. Malformed
data throws `FormatDataException`, an `IOException`.

## Not supported

- Everything that [no generator supports](codegen.md#not-supported-by-any-generator).
- Packet ids outside the `int` range.
- With `widen` (the default), 64-bit unsigned values above `Long.MAX_VALUE` are negative `long`s (each such field is
  reported as a warning); fixed-point values with a 64-bit unsigned base above that are read as negative numbers.
- With `checked`, enum members and variant tags above the signed range of their type.
- With `collections: array`, arrays of maps (Java can't create arrays of generic types).
- Maps as `java.util.Map`: they are lists of entries, which keep order and duplicate keys.
- Reading or writing NBT as a tree: NBT values are the raw bytes.
