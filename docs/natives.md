# Native types of your own

A [native type](dsl.md#native-types) is one the DSL can't describe: compressed blocks, bit-packed palettes, text
components. Besides the built-in `nbt`, a project or library can declare its own. The declaration says what the type
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
    kind: choice                 # choice | text | integer (choice when choices are given, else text)
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

Two native types can't have the same name, also not a built-in one or one from a library, and no definition can be
named like a native type. `format-studio natives` lists the native types a project can use and where they come from.

## Writing the plugin

The plugin is a class library that references `FormatStudio.Core` and implements `INativeType` with a public
parameterless constructor. Its `Name`, `Description` and `Parameters` are not used (the declaration's are); see
`NbtNative` for a complete example.

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

## Code for other generators

A native type's code comes from its declaration (or class) for the generators it was written for. Any project or
library can add code for other generators, e.g. Java for a type that ships only C#, or code for a
[plugin generator](generators.md#using-a-generator-in-a-project), with a `native-codegen` file among its sources:

```yaml
native-codegen: rgb               # the native type: built-in, the project's or a library's
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
