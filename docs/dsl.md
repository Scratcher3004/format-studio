# Projects and DSL

## Projects

A project is a folder with a `<name>.fstudio.yaml` file and one YAML file per type (packet, struct, enum, variant,
alias or bit field), so that changes are easy to review and diff.

```yaml
name: java-protocol
description: Sample project
version: "765"
defaults:
  endian: big            # little | big: default byte order of fixed-width numbers
  length: varint32       # prefix type of strings, bytes, arrays and maps without an explicit length
  string: utf8           # utf8 | mutf8 | ascii | latin1 | utf16le | utf16be
  nbt: java-network      # arguments of a native type, here the NBT flavor: java | java-network | bedrock | bedrock-network
                         # (no default; needed when nbt is used without one)
sources:                 # globs; a type in a later glob replaces the one with the same name in an earlier glob
  - imported/**/*.yaml
  - overrides/**/*.yaml
tests:                   # test cases, run by "format-studio test" (see testing.md; 'samples' is the old name)
  - tests/**/*.yaml
import:                  # importer settings, see "Mojang importer"
  mojang: { schema: ..., output: imported }
dependencies:            # libraries the project uses, see libraries.md
  - library: geo
    version: "1.2.0"
libraries: [libraries]   # folders with .fslib packages (the default)
protocols:               # optional: separate packet id spaces, see "Protocols"
  - name: LoginServerbound
    groups: [login, serverbound]
codegen:                 # code generation targets: csharp, csharp-stream, java-stream (see codegen.md)
  - name: csharp
    generator: csharp
    output: generated/csharp   # inside the project folder
    options: { namespace: My.Namespace, registry: Packets }
```

A library project (`kind: library`) holds definitions for other projects; see [libraries](libraries.md).

### Protocols

Many formats reuse packet ids: in the Minecraft protocol, id 0 is a different packet in every state and direction.
`protocols` splits the packets into id spaces of their own. A protocol's packets are those in all of its `groups`,
so a packet may belong to several protocols (e.g. one listing only `[status]`).

```yaml
protocols:
  - name: HandshakingServerbound     # an identifier; becomes a class name in generated code
    groups: [handshaking, serverbound]
    doc: The first packet of every connection.
  - name: PlayClientbound
    groups: [play, clientbound]
```

In the web UI, the *Protocols* page edits them (and suggests one protocol per group set of the packets). With
protocols, ids only have to be unique within each protocol, code generation writes a registry per protocol
(see [code generation](codegen.md)), and `identify` / `decode-packet` take `--protocol <name>`. Packets in no
protocol and protocols without packets are reported as warnings. Without protocols, ids must be unique among packets
with the same groups, and the single registry dispatches the first of the packets sharing an id.

## Definitions

Each file holds one definition. The key naming the kind carries the name; `title` and `doc` are allowed everywhere.
`skipCodegen: true` (or a list of codegen targets and generators) leaves a definition out of the generated code, e.g.
because it is written by hand; see [code generation](codegen.md#skipping-definitions).
Names are identifiers (`[A-Za-z_][A-Za-z0-9_]*`).

```yaml
packet: Particle                # or "struct:" for a type without id
id: 0x27
groups: [play, clientbound]     # free-form; ids must be unique per group set (or per protocol)
fields:
  - particleId: varint32        # short form: "- name: type"
  - name: data                  # long form
    type: ParticleData
    tag: particleId             # the variant's tag is the value of an earlier field
  - name: count
    type: varint32
    computed: len(matches)      # calculated when encoding
  - name: matches
    type: CommandMatch[count]   # length from an expression
    maxItems: 1000
  - name: tooltip
    type: string
    if: hasTooltip              # present only if the expression is true (no presence byte)
  - name: version
    type: u8
    const: 3                    # always this value
```

Integers (ids, enum values, variant tags, `const`, `default`, `min`, `max`) may be written in decimal (`39`), hex
(`0x27`) or binary (`0b100111`), optionally with a sign. The writer keeps the form of each value, and the builder shows
it the same way.

Field keys: `name`, `type`, `title`, `doc`, `if`, `const`, `computed`, `default`, `min`, `max`, `minLength`,
`maxLength`, `minItems`, `maxItems`, `pattern`, plus the type shortcuts `length`, `as`, `tag` and `encoding`, which
apply to the field's type.

```yaml
enum: GameMode
type: zigzag32          # default wire type; a use site can change it with "as"
flags: false
allowUnknown: true      # unknown values are kept, not reported
values:
  survival: 0
  creative: 1
  adventure: {value: 2, doc: Can't break blocks.}

variant: ParticleData   # tagged union
tag: varuint32          # tag read before the value; a primitive, a string type or {type: SomeEnum, as: u8}
cases:                  # without "tag:" the field using the variant must give one
  - name: block
    tag: 2              # a number, an enum member name when the tag is an enum, or the text for string tags
    type: varint32
  - name: none
    tag: 3              # no type: the case has no data

variant: PositionSource # string tags, e.g. registry names
tag: string             # a string type or an alias of one
cases:
  - {name: block, tag: "minecraft:block", type: BlockPos}
  - {name: entity, tag: "minecraft:entity", type: EntitySource}

alias: Identifier
type: {string: utf8, length: u16}

bitfield: Position      # first member in the most significant bits (lsbFirst: true for the reverse)
type: i64
fields:
  - {name: x, bits: 26, signed: true}
  - {name: z, bits: 26, signed: true}
  - {name: y, bits: 12, signed: true}
```

### Types

| Short form | Meaning |
|---|---|
| `bool`, `u8`, `i8`, `u16`…`i64`, `f32`, `f64` | Fixed width; add `le`/`be` to override the project byte order (`u16be`) |
| `varuint32`, `varuint64` | Unsigned LEB128 |
| `varint32`, `varint64` | Two's complement LEB128 (Java VarInt/VarLong) |
| `zigzag32`, `zigzag64` | Zigzag LEB128 (Bedrock "varint") |
| `uuid` | 16 bytes: two 64-bit halves, most significant first, in the byte order (`uuidle` for Bedrock) |
| `string` | Default encoding with the default length prefix |
| `bytes`, `bytes[16]`, `bytes[eof]` | Raw bytes (shown as hex) |
| `nbt`, `nbt(java)` | One NBT tag in the default or the given flavor (shown as SNBT); see [native types](#native-types) |
| `Name` | Another definition |
| `T[]` | Array with the default length prefix |
| `T[4]`, `T[u16]`, `T[eof]`, `T[count * 2]` | Fixed count, prefix type, until the end, expression over earlier fields |
| `T?` | Presence byte (`bool`), then the value if present |

Mapping forms: `{array: T, length: L}`, `{string: mutf8, length: L}` (`length: zero` for zero-terminated),
`{bytes: L}`, `{map: {key: K, value: V}, length: L}`, `{optional: T}`, `{sized: T, length: L}` (a value inside a
length-delimited section), `{fixed: i32, fraction: 5}` (fixed point), `{nbt: java-network}`,
`{native: name, args: [a, b]}`, `{type: Enum, as: zigzag32}`, `{type: Variant, tag: "expression"}`. A length `L` is a number, a primitive (prefix),
`eof`, `zero`, `default` or an expression.

`{array: Item, until: "index == 255"}` is a list without a count that ends with the first element for which the
condition is true; that element is part of the list (a sentinel such as Java's entity data terminator, or a
continuation bit as in `until: "!slot.more"`). The elements must be structs, and the condition uses their fields
(`_parent` is the struct holding the array). An external variant tag (`tag: "expression"`) whose cases have text tags
compares strings, e.g. a recipe type name stored before the recipe id.

### Native types

A native type is one the DSL can't describe; code that comes with Format Studio reads, writes, shows and generates
it. It is written as its name with arguments, `name(a, b)`, or as `{native: name, args: [a, b]}` (needed when an
argument contains `,` outside brackets, `{` or `:`). Some take a type as an argument, e.g. `zlib(Chunk[u8])`; such an
argument can also be a type mapping in the long form (`args: [{type: Shade, as: u16}]`). Arguments left out, or written as `default`, come from the project's
`defaults` under the type's name (one value, or a list), then from the parameter's own default; a required one that
is still missing is an error. The name of a native type always means the native type: a definition can't use it.

| Native type | Parameters | Value |
|---|---|---|
| `nbt` | `flavor`: `java` (big endian, named root), `java-network` (unnamed root, since 1.20.2), `bedrock` (little endian), `bedrock-network` (varints); no default | SNBT text, or `{"name": ..., "snbt": ...}` for a named root |

Generated code reads and writes NBT as its raw bytes. Projects and libraries can declare native types of their own,
implemented in .NET plugins: see [native types of your own](natives.md).

### Expressions

Used by `if`, `computed`, lengths and external tags: integer and float arithmetic, `== != < <= > >=`, `&& || !`,
`& | ^ ~ << >>`, `c ? a : b`, `a.b` (fields of a struct value), `a[i]`, `Enum::member`, `_parent`, `_root`, and the
functions `len(x)`, `bytelen(x)` (UTF-8 byte count) and `tagof(variant)`. Conditions and lengths can only use earlier
fields; computed fields can use any field.

### JSON values

The simulator represents values as JSON: numbers, `true`/`false`, strings, UUIDs as strings, bytes as hex strings,
enum members by name (or a number for unknown values; flags as `"a | b"`), structs and bit fields as objects,
variants as `{"caseName": value}` (or `"caseName"` for cases without data), absent optional/conditional values as
`null` or a missing key, maps as an object (string keys) or a list of `{key, value}`, NBT as SNBT text or
`{"name": ..., "snbt": ...}` when the root tag has a name.
