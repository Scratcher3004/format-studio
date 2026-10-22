# Native types of your own

A [native type](dsl.md#native-types) is one the DSL can't describe: compressed blocks, bit-packed palettes, text
components. Besides `nbt` of the [`minecraft` library](libraries.md#the-minecraft-library), a project or library can declare its own. The declaration says what the type
is called, which arguments it takes and how generated code handles it; a .NET plugin does the rest (decoding, encoding,
default and random values).

## Declaring a native type

A declaration is a YAML file among the project's sources with `native` at the top. Paths in it are relative to the
file and must stay inside the project.

```yaml
native: rgb                      # the name in type expressions: rgb, rgb(bgr), rgb[u8]
doc: A color as three bytes
plugin: ../plugins/Colors.dll    # the .NET assembly with the implementation (optional)
class: Colors.RgbType            # the class in it that implements FormatStudio.Natives.INativeType
minSize: 3                       # optional: the least bytes a value takes (otherwise the class says)
nullable: false                  # optional: whether null can be written (otherwise the class says)
parameters:
  - name: order
    kind: choice                 # choice | text | integer | type (choice when choices are given, else text)
    choices: [rgb, bgr]
    default: rgb                 # optional; without, the argument is required
    doc: Byte order
codegen:                         # per generator: csharp, csharp-stream, java-stream
  csharp:
    type: string
    read: 'global::{{namespace}}.Rgb.FromBytes({{reader}}.ReadU8(), {{reader}}.ReadU8(), {{reader}}.ReadU8(), "{{order}}")'
    write: '{{writer}}.WriteBytes(global::{{namespace}}.Rgb.ToBytes({{value}}, "{{order}}"))'
    files: [csharp/*.cs]         # globs; written into the generated output
    valueType: false             # C#: the type is a struct
  java-stream:
    type: String
    read: '{{namespace}}.Rgb.fromBytes({{reader}}.readU8(), {{reader}}.readU8(), {{reader}}.readU8(), "{{order}}")'
    write: '{{writer}}.writeBytes({{namespace}}.Rgb.toBytes({{value}}, "{{order}}"))'
    files: [java/*.java]
```

Arguments are completed as for `nbt`: from the reference, then `defaults.rgb` in the project, then the parameter's
default. The templates take the placeholders described in [code generation](codegen.md#native-types): `{{reader}}`,
`{{writer}}`, `{{value}}`, `{{namespace}}`, `{{runtime}}` and the arguments by name (`{{order}}`,
`{{order:pascal}}`). A generator the declaration has no `codegen` for asks the plugin class (`Codegen(generator)`).

Two native types can't have the same name, also not one from the `minecraft` library or another library, and no definition can be
named like a native type. `format-studio natives` lists the native types a project can use and where they come from.

## Writing the plugin

The plugin is a class library that references `FormatStudio.Core` and implements `INativeType` with a public
parameterless constructor. Its `Name`, `Description` and `Parameters` are not used (the declaration's are); see
`NbtNative` in `src/FormatStudio.Minecraft` for a complete example (it is a native type of a host library, which a
plugin isn't needed for).

```csharp
public sealed class RgbType : INativeType
{
    public string Name => "rgb";
    public string Description => "";
    public IReadOnlyList<NativeParameter> Parameters => [];

    public JsonNode Read(INativeReader reader, NativeArguments args)
    {
        byte[] bytes = [reader.Wire.ReadByte(), reader.Wire.ReadByte(), reader.Wire.ReadByte()];
        if (args["order"] == "bgr") Array.Reverse(bytes);
        var text = "#" + Convert.ToHexStringLower(bytes);
        reader.Display(text);            // shown in the byte tree
        return JsonValue.Create(text);
    }

    public void Write(INativeWriter writer, NativeArguments args, JsonNode? value) { ... throw writer.Error("..."); }
    public JsonNode DefaultValue(NativeArguments args) => JsonValue.Create("#000000");
    public JsonNode RandomValue(NativeArguments args, Random random, int depth) => JsonValue.Create($"#{random.Next(0x1000000):x6}");
    public NativeCodegen? Codegen(string generator) => null;
}
```

- Reference `FormatStudio.Core` without copying it to the output (`<Private>false</Private>` on the project reference,
  or `ExcludeAssets="runtime"` on a package): the plugin must use the Core that loads it. Format Studio and the .NET
  framework come from the host; other dependencies are loaded from the plugin's folder.
- Each plugin is loaded into a collectible load context of its own, from memory, so the files aren't locked. Rebuild
  the plugin and reload the project (the web UI's reload button, or any edit) to use the new build; a `.pdb` next to
  the assembly is loaded too, for debugging.
- Errors in values are reported with `reader.Error(...)` / `writer.Error(...)`. Other exceptions of the plugin are
  caught and reported as errors of the native type.

`GeneratedCodeVerifier` checks the generated code of a project that uses the type against the plugin, like any other
type (see [code generation](codegen.md#checking-generated-code)).

## Type parameters

A parameter of kind `type` takes a type: the native type's values contain values of that type, e.g. a compressed
block of a struct. Type arguments are written like other arguments, `zlib(Chunk)`, `zlib(Chunk[u8], 6)` (commas
inside brackets don't separate arguments), or in the mapping form for types that need it:
`{native: zlib, args: [{type: Shade, as: u16}]}`. They can't use fields of the struct they are in (no length
expressions), since the native type's code reads them on its own.

```yaml
native: zlib
plugin: ../plugins/Compression.dll
class: Compression.ZlibType
parameters:
  - name: T
    kind: type
    doc: The compressed value
  - name: level
    kind: integer
    default: 6
codegen:
  csharp:
    type: '{{T:type}}'           # the values are T's
    read: 'global::{{namespace}}.Zlib.Read({{pass-reader}}, {{T:read}})'
    write: 'global::{{namespace}}.Zlib.Write({{pass-writer}}, {{level}}, {{value}}, {{T:write}})'
    files: [csharp/Zlib.cs]
  java-stream:
    type: '{{T:type}}'
    read: '{{namespace}}.Zlib.read({{reader}}, {{T:read}})'
    write: '{{namespace}}.Zlib.write({{writer}}, {{level}}, {{value}}, {{T:write}})'
    files: [java/Zlib.java]
```

The plugin gets the type with `args.Type("T")` and reads and writes its values through the reader and writer, so
that every type works, those of libraries included:

```csharp
public JsonNode? Read(INativeReader reader, NativeArguments args)
{
    var bytes = Inflate(reader.Wire.Take(...));
    return reader.Decode(args.Type("T"), bytes, "content");   // a value from other bytes; reader.Read(type, name) reads in place
}

public void Write(INativeWriter writer, NativeArguments args, JsonNode? value)
{
    var bytes = writer.Encode(args.Type("T"), value, "content");   // writer.Write(type, value, name) writes in place
    ...
}

public JsonNode? DefaultValue(NativeArguments args) => args.DefaultValue("T");
public JsonNode? RandomValue(NativeArguments args, Random random, int depth) => args.RandomValue("T", random, depth);
```

In the templates, `{{T:type}}` is the type of `T`'s values in the target language, and `{{T:read}}` and
`{{T:write}}` are functions that read or write one value, for the runtime files to call:

| Generator | `{{T:read}}` | `{{T:write}}` |
|---|---|---|
| `csharp` | a static local function `T f(ref FormatReader reader)` | `void f(ref FormatWriter writer, T value)` |
| `csharp-stream` | `T f(FormatStreamReader reader)` | `void f(FormatStreamWriter writer, T value)` |
| `java-stream` | a `FormatReader.ValueReader<T>` | a `FormatWriter.ValueWriter<T>` |

In C# they convert to any delegate with that signature: the runtime file declares one (for spans, since
`FormatReader` is a `ref struct`) or takes a `Func`/`Action` (streams). A type whose code is just `{{T:type}}` is a
C# struct when `T` is.

## Code for other generators

A native type's code comes from its declaration (or class) for the generators it was written for. Any project or
library can add code for other generators, e.g. Java for a type that ships only C#, or code for a
[plugin generator](generators.md#using-a-generator-in-a-project), with a `native-codegen` file among its sources:

```yaml
native-codegen: rgb               # the native type: the project's or a library's
doc: Java for rgb
codegen:                          # as in a declaration
  java-stream:
    type: String
    read: '{{namespace}}.Rgb.fromBytes({{reader}}.readU8(), {{reader}}.readU8(), {{reader}}.readU8(), "{{order}}")'
    write: '{{writer}}.writeBytes({{namespace}}.Rgb.toBytes({{value}}, "{{order}}"))'
    files: [java/Rgb.java]
```

The code only adds generators: a type has one source of code per generator. Code for a generator the type has itself,
or that another file already adds, is an error, and the file is left out as a whole. `format-studio natives` lists
the added code under each type.

## In libraries

A library's native types are declared the same way. `format-studio pack` puts each declaration into the package with
the plugin, the dependencies it references from its folder (and their `.pdb` files) and the runtime files of the
generated code. A project that uses the library can use its native types, those of the library's own dependencies
included, by name. A library's `native-codegen` files are packed with their runtime files too and apply wherever the
library is loaded.

## Which plugins are loaded

Plugins run with the rights of Format Studio. The same setting applies to the plugins of native types and of
[code generators](generators.md#using-a-generator-in-a-project). The command line and the web UI load all of them
unless told otherwise:

| Setting | |
|---|---|
| `all` | Every plugin (the default) |
| `none` | No plugin |
| `<sha256>,<sha256>,...` | Only assemblies with these SHA-256 hashes (lowercase or uppercase hex); dependencies of a plugin need to be listed too |

The command line reads it from `FORMATSTUDIO_PLUGINS`, the web UI from `--plugins` (or `FORMATSTUDIO_PLUGINS`). A
server that opens projects it doesn't trust should use `none` or a list of hashes. The warning on a refused plugin
names its hash.

A native type whose plugin isn't loaded (none declared, not allowed, failed to load) still exists: references to it are
checked, the type builder offers it and code is generated. Only decoding and encoding report that it can't, and new
values start out empty.
