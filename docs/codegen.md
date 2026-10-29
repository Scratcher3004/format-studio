# Code generation

A codegen target names a generator (language and I/O model), an output folder inside the project and the generator's
options. Targets live in the `codegen` section of the project file; the web UI's *Code generation* page adds, edits and
removes them (and previews any generator with its default options before a target exists).

```yaml
codegen:
  - name: csharp
    generator: csharp
    output: generated/csharp     # inside the project folder
    options: { namespace: My.Protocol }
  - name: java
    generator: java-stream
    output: generated/java
    options: { package: com.example.protocol }
```

| Generator | Output | |
|---|---|---|
| `csharp` | C# (spans) | `ref struct` reader over `ReadOnlySpan<byte>`, writer over `IBufferWriter<byte>`. See [C#](csharp.md). |
| `csharp-stream` | C# (streams) | Reader and writer classes over `System.IO.Stream`. See [C#](csharp.md). |
| `java-stream` | Java 8+ (streams) | `FormatReader`/`FormatWriter` over `InputStream`/`OutputStream`. See [Java](java.md). |

Projects and libraries can add generators of their own from .NET plugins (see [writing a code
generator](generators.md#using-a-generator-in-a-project)). `format-studio generators --verbose` lists the project's
generators with all their options. Unknown options are reported as
warnings. `format-studio show <type> --target <target|generator>` prints the code of one type, `format-studio generate`
writes the targets.

All generators share the same model: one file per definition (aliases are inlined), a registry with the packet id
dispatch (with [protocols](dsl.md#protocols), a nested class per protocol in the registry class, e.g.
`Packets.LoginServerbound`), and the same treatment of computed, const and conditional fields, `until` arrays, external and string tags,
and length checks before allocating. A language-independent layer (`CodeEmitter`) walks the definitions and fields and
translates expressions; each language adds its syntax and I/O. Other languages can be built on it the same way, see
[writing a code generator](generators.md).

The output only goes to the target's folder inside the project, and only files starting with the generated-file
marker are replaced or deleted (others are reported and left alone). Copying the reviewed output into a source tree is
deliberately a manual step.

## Library definitions

The library definitions a project uses are generated with it, in a namespace or package of their own per library
(`My.Protocol.Geo`); a dependency can skip a library or name its namespace. See [libraries](libraries.md#code-generation).

## Skipping definitions

A definition that is written by hand (or comes from a library) is skipped with `skipCodegen`. The generated code still
references it, so it must provide the same `Read`/`Write` methods (`read`/`write` in Java).

```yaml
struct: Slot
skipCodegen: true                  # skipped by every target
fields: [...]
```

```yaml
struct: Slot
skipCodegen: [java-stream, cs]     # skipped by every java-stream target and by the target named cs
fields: [...]
```

The list names targets or generators; names that are neither are reported as warnings. In the web UI, the
definition's *Code* tab has a *Code generation* setting (saved on its own; comments in the file stay), and each
target's editor lists the types it skips. (`skipCodegen` replaced the targets' `exclude` option, which is now ignored
with a warning; the target editor removes it when saving.)

## Binding definitions to existing types

A definition can stand for a type that already exists in the target language: a `Vec3` struct for Minecraft's
`net.minecraft.world.phys.Vec3`, an `Identifier` alias for its `Identifier` class. A `bind` file among the project's
sources says, per generator, which type the values have and how they are read and written. The definition keeps
describing the wire format, so the simulator, the byte tree and the test cases work as before; only the generated
code changes: the definition isn't generated, and its references use the binding.

```yaml
bind: Vec3                       # a definition: the project's own, or a library's (geo.Vec3)
doc: Minecraft's Vec3
codegen:                         # per generator, like the code of a native type
  java-stream:
    type: net.minecraft.world.phys.Vec3
    read: '{{namespace}}.Vec3s.read({{reader}})'
    write: '{{namespace}}.Vec3s.write({{writer}}, {{value}})'
    files: [java/Vec3s.java]     # written into the output, like a native type's files
  netty:                         # a generator that composes codecs takes a codec instead of read/write
    type: net.minecraft.world.phys.Vec3
    codec: net.minecraft.world.phys.Vec3.STREAM_CODEC
```

The keys and placeholders are those of a [native type's code](natives.md#declaring-a-native-type): `type`, `read`,
`write`, `files`, `valueType` (C#), and `codec`. `read` and `write` can be left out when there is a `codec`; a
generator that reads and writes values then reports the uses of the type as errors. A bound enum can also give
`value`, an expression of a value's wire number (`'{{value}}.ordinal()'`), so that conditions can compare it with the
enum's members (`if: mode == Mode::create`).

Generators the binding has no code for generate the definition as usual. A definition can be bound once per
generator; a library's bindings apply wherever the library is loaded (see [libraries](libraries.md#code-generation)).
What a bound type can't do in generated code, since its code is opaque, is reported as an error of the definition that
uses it:

- Expressions can't look into it: no `pos.x`, `len(name)`, `tagof(...)` or `until` conditions over its fields. Values
  of bound enums with a `value` can be compared and calculated with, but not when they are optional or conditional.
- It can't be the type of a `const` or computed field, a bit field member, or a variant's tag, and it can't be used
  with `as` or an external tag.
- A bound packet is left out of the packet registry (with a warning): the registry returns the generated packets.

## Not supported by any generator

Definitions that use these are reported as errors and not generated; the other definitions are.

- `_parent` and `_root` in expressions, and members of bit fields in expressions (`flags.hasMax`).
- String encodings other than UTF-8, ASCII and Latin-1 (`mutf8`, `utf16le`, `utf16be`).
- Lengths: `zero` for bytes; `eof` and `until` for maps; `until` for anything but arrays of structs (and the condition
  may only use fields stored in the element); `zero` and `eof` for `sized` values.
- `const` of other types than integers, floats, bools, strings and enums (e.g. `uuid`, bytes, arrays).
- Integer arithmetic beyond 64-bit signed integers: expressions use `long`, so 64-bit unsigned values above
  2^63 - 1 don't compare or calculate correctly in conditions, lengths and computed fields.
- Packets that share an id in a project without [protocols](dsl.md#protocols): the registry dispatches by id alone,
  so only the first is dispatched (reported as a warning). Declare protocols to get a registry per state/direction.
- NBT as a tree: NBT values are kept as raw bytes.
- Native types without code for the generator (those of the `minecraft` library have code for every generator).

## Native types

Each [native type](dsl.md#native-types) says how generated code reads and writes its values: a type in the target
language and read/write templates, plus source files the code needs. The code of a type argument (`zlib(Chunk)`) is
generated as a function that the template passes on ([type parameters](natives.md#type-parameters)). Those files are written into the output once per
native type used, in `Natives/` (C#, namespace `<namespace>.Natives`) or in the package `<package>.natives` (Java), with
the generator's marker like every generated file (`nbt` of the `minecraft` library has such files). A type without
code for a generator can get it from a [`native-codegen` file](natives.md#code-for-other-generators); otherwise its
uses are errors of the definitions.

## Checking generated code

`FormatStudio.Testing` compiles a target's output and checks it against the simulator: random values encoded by the
simulator must be read and written back byte for byte by the generated code.

```csharp
var project = FormatProject.Load("path/to/project");
var csharp = project.Settings.Codegen.Single(t => t.Name == "csharp");
Assert.Empty(GeneratedCodeVerifier.CheckAgainstSimulator(project, csharp, typeof(MyReader).Assembly, iterations: 5));

// Java needs a JDK: JavaToolchain.Find() looks at FORMAT_STUDIO_JDK, JAVA_HOME and the PATH.
var java = project.Settings.Codegen.Single(t => t.Name == "java");
Assert.Empty(GeneratedCodeVerifier.CheckJavaAgainstSimulator(project, java, JavaToolchain.Find()!, iterations: 5));
```

The repository's tests run every generator this way; the Java tests are skipped when no JDK is found. A project's own
test cases run against its targets with `format-studio test --target <target>`, or as unit tests with `ProjectSuite`
(see [testing](testing.md#generated-code)).
