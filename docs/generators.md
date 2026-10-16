# Writing a code generator

A generator for another language is a class that implements `ICodeGenerator` and, for the code itself, derives from
`CodeEmitter` in `FormatStudio.Codegen` (in `FormatStudio.Core`). The C# and Java generators are built on it.
`CodeEmitter` does what every language needs the same way, and calls the language's hooks for the rest.

| `CodeEmitter` does | The language does (hooks) |
|---|---|
| Walks the definitions: the project's own, those of libraries it uses; skips excluded ones, inlines aliases | One file per definition: `Struct`, `Enum`, `Variant`, `Bitfield`, its path (`PathOf`), shared files (`Finish`) |
| Reports a `CodegenException` as an error of the definition and goes on with the next | Throws `CodegenException` for what the language can't express |
| Walks the fields of a struct in wire order (`ReadFields`, `WriteFields`): locals, conditions, constants, ranges, computed fields first when writing | Statements: `DeclareLocal`, `OpenIf`/`CloseIf`, `Assign`, `CheckConstant`, `CheckRange`, `DeclareComputed`, `RequirePresent`, `Constant` |
| Dispatches on the kind of type (`Read`, `Write`), fills in native types from their templates | `ReadPrimitive` ... `ReadDefinition`, `WritePrimitive` ... `WriteDefinition`, one per kind of type |
| Lengths: fixed, prefix or expression (`ReadCount`, `WriteCount`) | `ReadLengthPrefix`, `CheckCount`, `WriteLengthPrefix`, `CheckFixedCount` |
| Translates DSL expressions, typed (`Translate`, `Expression`, `Condition`), with the names in scope while reading and writing | Their syntax: `NumberLiteral`, `StringLiteral`, `EnumConstant`, `Convert`, `IndexAccess`, `Length`, `ByteLength`, `TagNumber`, `TagText`, `TextCompare`, `MemberName` |

Hooks that read return an expression and add the statements it needs to the `CodeWriter`; hooks that write add
statements. `Temp("name")` gives fresh local names, numbered per file. The `Context` passed along names the reader and
writer (a sized value reads from a slice: `context.WithReader(...)`) and the names expressions can use.

Helpers for the rest: `Generated` (the definitions to generate), `Definition(name)`, `IsExcluded`, `DependencyOf` (how
the project uses a library, e.g. its namespace), `Method(primitive)` (`U8`, `I16LE`, `VarUInt32`, ...), `MinSize`,
`Native(ref)` and `NativeTemplate`, `Registries` (the packet dispatch per protocol), `TagWire`/`TagEnum` of variants,
`SelfContainingTypes`, `Describe(type)`.

```csharp
public sealed class SketchGenerator : ICodeGenerator
{
    public string Name => "sketch";                    // the generator of codegen targets, and what native types are asked for
    public string Title => "Sketch";
    public string Language => "text";
    public string Description => "Pseudo-code";
    public string DefaultOutput => "generated/sketch";
    public string Marker => "# sketch";                // first line of every file; only such files are replaced
    public IReadOnlyList<CodegenOption> Options { get; } = [CodegenOption.Bool("ranges", "Ranges", true, "Check min and max.")];

    public GeneratedOutput Generate(FormatProject project, JsonObject options, IReadOnlySet<string> skipped) =>
        new SketchEmitter(project, skipped, new OptionReader(options, Options).Bool("ranges")).Run();
}

internal sealed class SketchEmitter(FormatProject project, IReadOnlySet<string> skipped, bool ranges) : CodeEmitter(project, skipped)
{
    protected override string Struct(StructDef structDef)
    {
        var w = new CodeWriter();
        w.Line(Marker).Line($"struct {structDef.Name}:");
        w.Indent();
        w.Line("def read(reader):");
        w.Indent();
        w.Line("result = new()");
        ReadFields(structDef, w, f => f.IsInput ? "result." + f.Name : "_" + f.Name);
        w.Line("return result");
        // ...
    }

    protected override void OpenIf(string condition, CodeWriter w) { w.Line($"if {condition}:"); w.Indent(); }
    protected override void CloseIf(CodeWriter w) => w.Outdent();
    protected override string ReadPrimitive(PrimitiveRef primitive, CodeWriter w, Context context) => $"{context.Reader}.{Method(primitive)}()";
    // ... the other hooks
}
```

`GeneratorSdkTests` has the whole of this generator: it is compiled into an assembly of its own, against the public
API only.

Native types have code for a generator by its `Name` (see [native types](natives.md)); the `{{pass-reader}}` and
`{{pass-writer}}` placeholders come from `PassReader`/`PassWriter`, and a native write becomes a statement through
`Statement`.

## Using a generator in a project

A generator comes into a project as a .NET plugin. A YAML file among the project's sources declares it, with paths
relative to the file:

```yaml
generator: sketch                 # the name targets use (generator: sketch) and native types have code for
doc: Pseudo-code for reviews      # optional; otherwise the class's Description
plugin: ../plugins/Sketch.dll     # the .NET assembly
class: Sketch.SketchGenerator     # the class in it that implements FormatStudio.Codegen.ICodeGenerator
```

The class needs a public parameterless constructor and a `Marker`; its title, language, default output and options
are shown as for the built-in generators. The plugin is built and loaded like a [native type's](natives.md#writing-the-plugin):
it references `FormatStudio.Core` without copying it, gets a load context of its own and is reloaded when it is
rebuilt. The same [plugin policy](natives.md#which-plugins-are-loaded) decides whether it is loaded.

Each project has its own generators: the built-in ones, those it declares and those of its libraries. A name can't be
used twice, also not a built-in one. The command line (`generate`, `show --target`, `generators`) and the web UI use
the project's generators, and `skipCodegen` and the `codegen` of native types can name them.

Without its plugin (not allowed, failed to load) the generator still exists: targets naming it stay valid, but
generating reports why it can't, and nothing is written. Exceptions of `Generate` other than
`FormatDefinitionException` (an invalid option) are reported as errors of the generator.

A library ships its generators: `format-studio pack` puts each declaration into the package with the plugin and the
dependencies it references from its folder, and projects that use the library can use the generator.

## Compatibility

The protected members of `CodeEmitter` are a public API, versioned by `CodeEmitter.ApiVersion` (now 1). The version is
raised when a hook is added or a member changes incompatibly.
