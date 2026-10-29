# Netty generator (Minecraft StreamCodecs)

`netty` generates Java records with a Minecraft `StreamCodec` each, the way mods write their network payloads by
hand. It is a generator of the [`minecraft` library](libraries.md#the-minecraft-library), so a project needs
`dependencies: [minecraft]` to use it. It targets Minecraft 26.1 (Mojang's names, `Identifier`, `StreamCodec`,
`ByteBufCodecs`) and needs Java 16.

```yaml
dependencies: [minecraft]
codegen:
  - name: mod
    generator: netty
    output: generated/netty
    options:
      package: com.example.mod.network
```

The output is the [java-stream](java.md) code, as records with `java.util.List`s (the `records` and `collections`
options are fixed), plus these members:

- Every struct, enum, bit field and variant with its own tag has
  `public static final StreamCodec<RegistryFriendlyByteBuf, T> CODEC`.
- A packet with a [text id](dsl.md#definitions) (`id: "mod:move"`) is a `CustomPacketPayload`: it has
  `TYPE = new CustomPacketPayload.Type<>(Identifier.parse(ID))` and `type()`. Register it with Fabric as usual, e.g.
  `PayloadTypeRegistry.playC2S().register(Move.TYPE, Move.CODEC)`.

```java
public record Move(Mode mode, int transaction, java.util.List<Anchor> targets) implements FormatTextPacket, CustomPacketPayload {
    public static final String ID = "mod:move";
    ...
    public static final StreamCodec<RegistryFriendlyByteBuf, Move> CODEC = StreamCodec.composite(
            Mode.CODEC, Move::mode,
            ByteBufCodecs.VAR_INT, Move::transaction,
            Anchor.CODEC.apply(ByteBufCodecs.list(8)), Move::targets,
            Move::new);

    public static final CustomPacketPayload.Type<Move> TYPE = new CustomPacketPayload.Type<>(Identifier.parse(ID));
    ...
}
```

| Option | Default | |
|---|---|---|
| `strategy` | `auto` | `auto`: `StreamCodec.composite` where a struct allows it, else a codec over the generated read and write code. `composite`: a struct that can't be composed is an error. `manual`: always the read and write code |
| `buffer` | `net.minecraft.network.RegistryFriendlyByteBuf` | The buffer type of the codecs |

The other options are those of java-stream: `package`, `registry`, `unsigned`, `validateRanges`, `validateStrings`,
`includeRuntime`, `runtimePackage`.

## Composite and manual codecs

A struct is a `StreamCodec.composite` when it has at most 12 fields, none of them const, computed or conditional
(and no ranges to check with `validateRanges`), it doesn't contain itself, and every field's type has a codec:

| Type | Codec |
|---|---|
| `bool`, `i8`, `i16`, `i32`, `i64`, `f32`, `f64` (big endian) | `ByteBufCodecs.BOOL`, `BYTE`, `SHORT`, `INT`, `LONG`, `FLOAT`, `DOUBLE` |
| `varint32`, `varint64` | `VAR_INT`, `VAR_LONG` |
| `u16` (big endian), `u8`, with `unsigned: widen` | `UNSIGNED_SHORT`; `BYTE` mapped to an `int` |
| `uuid` (big endian) | `UUIDUtil.STREAM_CODEC` |
| UTF-8 `string` with a `varint32` length | `ByteBufCodecs.stringUtf8(max)` with a `max`, else `STRING_UTF8` (Minecraft's limit of 32767 characters) |
| `bytes` with a `varint32` length | `BYTE_ARRAY` |
| `T[]` with a `varint32` length | `T`'s codec `.apply(ByteBufCodecs.list())`, `list(maxItems)` with the field's `maxItems` |
| `T?` | `NettyFormat.nullable(...)`: Minecraft's optional, as the value or null |
| a struct, enum, bit field or variant with its own tag | its `CODEC` (skipped types must have one too) |
| `minecraft.Identifier`, `Vec3`, `BlockPos`, `ChunkPos` | their `STREAM_CODEC`s ([the library's definitions](libraries.md#the-minecraft-library)) |
| a [bound](codegen.md#binding-definitions-to-existing-types) type or native type | its `codec`: `codegen: {netty: {type: ..., codec: ...}}` |
| `nbt` (`java-network` only) | `ByteBufCodecs.TAG`; values are `net.minecraft.nbt.Tag`s |

Anything else (little-endian or other unsigned numbers, maps, fixed point, `sized`, other string encodings or
lengths) makes the struct's codec manual: `NettyFormat.codec(T::read, T::write)`, which reads and writes with the
generated code on the `ByteBuf` itself. Enums, bit fields and variants always have such codecs. A bound type with only
a `codec` is read and written inside manual codecs with `NettyFormat.decode`/`encode`, which hand it the `ByteBuf`;
that doesn't work inside a `sized` value (a `FormatDataException` when reading). Errors in data read by the generated
code are thrown as `UncheckedIOException`s.

`NettyFormat` is written into the runtime package with the rest of the [Java runtime](java.md#runtime), and `nbt`'s
code into the `natives` package. Members named `CODEC`, `TYPE` or `type` get a `Value` suffix (enum members a `_`).

## Checking the generated code

The repository's tests compile the output against small stand-ins of the Minecraft classes it uses
(`tests/FormatStudio.Tests/MinecraftStubs.cs`) and the real netty-buffer, and check every type's `CODEC` byte for byte
against the simulator. netty-buffer comes from the folder `FORMAT_STUDIO_NETTY` or is downloaded from Maven Central
once; without it (or a JDK) the tests are skipped. `format-studio test --target` doesn't run test cases against netty
targets.

## Not supported

- Everything that [java-stream doesn't support](java.md#not-supported).
- NBT other than `java-network`, the NBT Minecraft's network codecs read.
- Writing codecs for existing classes from the DSL (a binding gives the codec of an existing class instead).
- Registering payloads: the generator doesn't know a packet's direction and protocol phase for Fabric's
  `PayloadTypeRegistry`.
