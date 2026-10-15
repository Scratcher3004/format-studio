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

`format-studio generators --verbose` lists the generators with all their options. Unknown options are reported as
warnings. `format-studio show <type> --target <target|generator>` prints the code of one type, `format-studio generate`
writes the targets.

All generators share the same model: one file per definition (aliases are inlined), a registry with the packet id
dispatch (with [protocols](dsl.md#protocols), a nested class per protocol in the registry class, e.g.
`Packets.LoginServerbound`), and the same treatment of computed, const and conditional fields, `until` arrays, external and string tags,
and length checks before allocating. Expressions are translated once by a language-independent layer
(`Codegen/Emitter.cs`); each language adds its syntax and I/O.

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

## Not supported by any generator

Definitions that use these are reported as errors and not generated; the other definitions are.

- `_parent` and `_root` in expressions, and members of bit fields in expressions (`flags.hasMax`).
- String encodings other than UTF-8 (`mutf8`, `ascii`, `latin1`, `utf16le`, `utf16be`).
- Lengths: `zero` for bytes; `eof` and `until` for maps; `until` for anything but arrays of structs (and the condition
  may only use fields stored in the element); `zero` and `eof` for `sized` values.
- `const` of other types than integers, floats, bools, strings and enums (e.g. `uuid`, bytes, arrays).
- Integer arithmetic beyond 64-bit signed integers: expressions use `long`, so 64-bit unsigned values above
  2^63 - 1 don't compare or calculate correctly in conditions, lengths and computed fields.
- Packets that share an id in a project without [protocols](dsl.md#protocols): the registry dispatches by id alone,
  so only the first is dispatched (reported as a warning). Declare protocols to get a registry per state/direction.
- NBT as a tree: NBT values are kept as raw bytes.

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

The repository's tests run every generator this way; the Java tests are skipped when no JDK is found.
