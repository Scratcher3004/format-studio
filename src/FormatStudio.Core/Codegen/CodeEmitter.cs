using FormatStudio.Dsl;
using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Natives;
using FormatStudio.Projects;

namespace FormatStudio.Codegen;

/// <summary>A definition can't be generated; reported as an error of that definition.</summary>
public sealed class CodegenException(string message) : Exception(message);

/// <summary>
/// The base of code generators (the generator SDK): the language-independent part of generating code for a project.
/// It walks the definitions and reports errors per definition, dispatches on the kinds of types, walks the fields of
/// structs in wire order, completes native types and translates DSL expressions. A language derives from it and
/// implements the hooks: the declarations of the definitions, the reading and writing of each kind of type, and the
/// syntax of expressions and statements. The C# and Java generators are built on it.
/// </summary>
/// <remarks>
/// Hooks get a <see cref="CodeWriter"/> to add statements to and return expressions as text; names of temporaries come
/// from <see cref="Temp"/>. Throw <see cref="CodegenException"/> for what a language can't express: the definition is
/// reported and skipped. The protected members are a public API, versioned by <see cref="ApiVersion"/>.
/// </remarks>
/// <param name="exclude">Definitions that are referenced but not generated, by qualified name (see <see cref="CodeGeneration.Excluded"/>).</param>
public abstract class CodeEmitter(FormatProject project, IReadOnlySet<string> exclude)
{
    /// <summary>
    /// The version of this API. It is raised when a hook is added or a protected member changes incompatibly; a generator
    /// built against another version may not load.
    /// </summary>
    public const int ApiVersion = 2;

    private int _temp;
    private readonly Dictionary<string, (INativeType Type, NativeCodegen Codegen)> _natives = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeCodegen> _bindings = new(StringComparer.Ordinal);

    protected FormatProject Project => project;
    protected GeneratedOutput Output { get; } = new();

    public GeneratedOutput Run()
    {
        foreach (var definition in Generated.OrderBy(d => d.QualifiedName, StringComparer.Ordinal))
        {
            if (IsExcluded(definition) || definition is AliasDef) continue;
            // Temporaries are numbered per file, so that a change to one type doesn't renumber the others.
            _temp = 0;
            Current = definition;
            try
            {
                CheckBindings(definition);
                var code = definition switch
                {
                    StructDef structDef => Struct(structDef),
                    EnumDef enumDef => Enum(enumDef),
                    VariantDef variant => Variant(variant),
                    BitfieldDef bitfield => Bitfield(bitfield),
                    _ => throw new CodegenException($"Unsupported definition {definition.Kind}"),
                };
                Output.Files.Add(new GeneratedFile(PathOf(definition), code, definition.QualifiedName));
            }
            catch (Exception e) when (e is CodegenException or ExprException)
            {
                Output.Diagnostics.Error($"{definition.QualifiedName}: {e.Message}", definition.Source);
            }
        }

        Current = null;
        var runtimeFiles = _natives.Values.OrderBy(n => n.Type.Name, StringComparer.Ordinal)
            .Select(n => ($"the native type '{n.Type.Name}'", n.Codegen))
            .Concat(_bindings.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => ($"the binding of '{b.Key}'", b.Value)));
        var written = new Dictionary<string, (string Owner, string Content)>(StringComparer.Ordinal);
        foreach (var (owner, codegen) in runtimeFiles)
        {
            foreach (var file in codegen.Files ?? [])
            {
                var path = NativeFilePath(file.Name);
                var content = NativeTemplate(file.Content, null, new Dictionary<string, string>());
                if (written.TryGetValue(path, out var other))
                {
                    // Two bindings (or types) may share a runtime file; different files of the same name would overwrite each other.
                    if (other.Content != content) Output.Diagnostics.Error($"{other.Owner} and {owner} both have a runtime file {file.Name}, with different content");
                    continue;
                }

                written[path] = (owner, content);
                Output.Files.Add(new GeneratedFile(path, $"{Marker}\n// Runtime of {owner}, written by format-studio. Do not edit.\n" + content));
            }
        }

        Finish();
        return Output;
    }

    /// <summary>The generator's name, e.g. <c>csharp</c>, which native types are asked for their code (<see cref="INativeType.Codegen"/>).</summary>
    protected abstract string Generator { get; }

    /// <summary>The first line of every generated file.</summary>
    protected abstract string Marker { get; }

    /// <summary>The namespace or package of the runtime files of native types (<c>{{namespace}}</c>).</summary>
    protected abstract string NativeNamespace { get; }

    /// <summary>The namespace or package of the runtime (<c>{{runtime}}</c>).</summary>
    protected abstract string RuntimeNamespace { get; }

    /// <summary>The path of a runtime file of a native type inside the output folder.</summary>
    protected abstract string NativeFilePath(string fileName);

    /// <summary>The path of a definition's file inside the output folder.</summary>
    protected abstract string PathOf(TypeDef definition);

    /// <summary>The file of a struct or packet (usually with <see cref="ReadFields"/> and <see cref="WriteFields"/>).</summary>
    protected abstract string Struct(StructDef structDef);

    /// <summary>The file of an enum.</summary>
    protected abstract string Enum(EnumDef enumDef);

    /// <summary>The file of a variant.</summary>
    protected abstract string Variant(VariantDef variant);

    /// <summary>The file of a bit field.</summary>
    protected abstract string Bitfield(BitfieldDef bitfield);

    /// <summary>Adds the files that don't belong to a definition (registry, runtime).</summary>
    protected virtual void Finish()
    {
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The definition whose file is being generated; null for shared files (registry, runtime).</summary>
    protected TypeDef? Current { get; private set; }

    /// <summary>
    /// Types written by hand or coming from elsewhere (skipped libraries): referenced, but not generated (and aliases
    /// among them are not inlined). <paramref name="reference"/> is a type name as written in the definitions.
    /// </summary>
    protected bool IsExcluded(string reference) => IsExcluded(Definition(reference));

    protected bool IsExcluded(TypeDef definition) => exclude.Contains(definition.QualifiedName);

    /// <summary>How the project uses the library of a definition, or null for the project's own definitions.</summary>
    protected LibraryDependency? DependencyOf(TypeDef definition) => definition.Owner is { } owner
        ? project.Settings.Dependencies.FirstOrDefault(d => d.Library == owner.Name)
        : null;

    private List<TypeDef>? _generated;

    /// <summary>
    /// The definitions code is generated for: the project's own, and the library definitions they use (directly or through
    /// other library definitions), except those of skipped libraries.
    /// </summary>
    protected IReadOnlyList<TypeDef> Generated => _generated ??= CollectGenerated();

    private List<TypeDef> CollectGenerated()
    {
        var result = new List<TypeDef>();
        var seen = new HashSet<TypeDef>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<TypeDef>(project.Types.Values);
        while (queue.TryDequeue(out var definition))
        {
            if (!seen.Add(definition) || definition.Owner != null && IsExcluded(definition)) continue;
            // The binding's code replaces the definition's, so neither it nor the types it uses are generated for it.
            if (IsBound(definition)) continue;
            result.Add(definition);
            foreach (var name in TypeReferences.Of(definition))
                if (project.Find(name) is { Owner: not null } used) queue.Enqueue(used);
        }

        return result;
    }

    protected TypeDef Definition(string name) =>
        project.Find(name) ?? throw new CodegenException($"Unknown type '{name}'");

    // ------------------------------------------------------------------ bindings

    /// <summary>Whether a definition is bound to an existing type for this generator (<see cref="TypeBinding"/>), so it isn't generated.</summary>
    protected bool IsBound(TypeDef definition) => project.Binding(definition, Generator) != null;

    /// <summary>
    /// The code of a reference to a definition that is bound to an existing type for this generator, or null when the
    /// definition is generated. Its runtime files are written into the output. A bound type can't be used with
    /// <c>as</c> or an external tag, which its code doesn't know of.
    /// </summary>
    protected NativeCodegen? Binding(NamedRef named)
    {
        var definition = Definition(named.Name);
        if (project.Binding(definition, Generator) is not { } codegen) return null;
        if (named.As != null) throw new CodegenException($"{named.Name} is bound to an existing type, whose wire type can't be changed with 'as'");
        if (named.Tag != null) throw new CodegenException($"{named.Name} is bound to an existing type, which can't take an external tag");
        _bindings.TryAdd(definition.QualifiedName, codegen);
        return codegen;
    }

    /// <summary>The type of a bound definition's values in the language (see <see cref="Binding"/>), or null when it is generated.</summary>
    protected string? BoundType(NamedRef named) =>
        Binding(named) is { } codegen ? NativeTemplate(codegen.Type, null, new Dictionary<string, string>()) : null;

    /// <summary>Fails for code that composes codecs only, when values are read and written.</summary>
    private static NativeCodegen ReadWrite(NativeCodegen codegen, string what) => codegen.CanReadWrite
        ? codegen
        : throw new CodegenException($"{what} has only a codec, no read and write code");

    /// <summary>
    /// <see cref="FormatProject.Unalias"/>, except that an alias bound to an existing type stays: its values are those of
    /// the binding, not of the alias's type.
    /// </summary>
    protected TypeRef Unalias(TypeRef type)
    {
        for (var depth = 0; depth < 64; depth++)
        {
            if (type is not NamedRef { As: null, Tag: null } named || project.Find(named.Name) is not AliasDef alias || IsBound(alias)) return type;
            type = alias.Target;
        }

        throw new CodegenException($"Alias cycle at {type}");
    }

    /// <summary>
    /// The uses of bound types that the code of a definition can't express: constants and computed values (their
    /// expressions are numbers, strings and enums of the DSL), bit field members and variant tags of a bound enum.
    /// </summary>
    private void CheckBindings(TypeDef definition)
    {
        string? Bound(TypeRef type) => Unalias(type) is NamedRef named && IsBound(Definition(named.Name)) ? named.Name : null;
        switch (definition)
        {
            case StructDef structDef:
                foreach (var field in structDef.Fields.Where(f => !f.IsInput))
                {
                    if (Bound(field.Type) is { } name)
                        throw new CodegenException($"{field.Name}: a {(field.Const != null ? "const" : "computed")} field can't be of {name}, which is bound to an existing type");
                }

                break;
            case BitfieldDef bitfield:
                foreach (var member in bitfield.Members.Where(m => m.Type is not (null or "bool")))
                {
                    if (IsBound(Definition(member.Type!)))
                        throw new CodegenException($"{member.Name}: a bit field member can't be of {member.Type}, which is bound to an existing type");
                }

                break;
            case VariantDef { Tag: { } tag } when Bound(tag) is { } tagName:
                throw new CodegenException($"The tag of a variant can't be {tagName}, which is bound to an existing type");
        }
    }

    /// <summary>A native type reference with its arguments, and its code for this generator.</summary>
    protected (NativeCodegen Codegen, NativeArguments Args) Native(NativeRef native)
    {
        BoundNative bound;
        try
        {
            bound = project.Bind(native);
        }
        catch (FormatDefinitionException e)
        {
            throw new CodegenException(e.Message);
        }

        var codegen = project.Natives.Codegen(bound.Type, Generator) ?? throw new CodegenException($"The native type {native.Name} has no code for {Generator}");
        _natives.TryAdd(bound.Type.Name, (bound.Type, codegen));
        return (codegen, bound.Args);
    }

    /// <summary>
    /// Fills a <see cref="NativeCodegen"/> template outside reading and writing code (a type, a runtime file);
    /// <paramref name="values"/> are the placeholders of the call site.
    /// </summary>
    protected string NativeTemplate(string template, NativeArguments? args, IReadOnlyDictionary<string, string> values) =>
        NativeTemplate(template, args, values, null, null);

    /// <summary>
    /// Fills a <see cref="NativeCodegen"/> template; <paramref name="values"/> are the placeholders of the call site. The
    /// placeholders of type arguments are filled from <see cref="TypeArgument"/>, and in reading and writing code
    /// (<paramref name="w"/> given) from <see cref="ReadFunction"/> and <see cref="WriteFunction"/>.
    /// </summary>
    protected string NativeTemplate(string template, NativeArguments? args, IReadOnlyDictionary<string, string> values, CodeWriter? w, Context? context)
    {
        var all = new Dictionary<string, string>(values, StringComparer.Ordinal)
        {
            ["namespace"] = NativeNamespace,
            ["runtime"] = RuntimeNamespace,
        };
        if (args != null) AddTypeArguments(template, args, all, w, context);
        return NativeTypes.Fill(template, args, all);
    }

    private void AddTypeArguments(string template, NativeArguments args, Dictionary<string, string> values, CodeWriter? w, Context? context)
    {
        foreach (var placeholder in NativeTypes.Placeholders(template).Distinct())
        {
            var colon = placeholder.IndexOf(':');
            if (colon < 0 || values.ContainsKey(placeholder)) continue;
            var (name, modifier) = (placeholder[..colon], placeholder[(colon + 1)..]);
            if (modifier is not ("type" or "read" or "write")
                || !args.Parameters.Any(p => p.Name == name && p.Kind == NativeParameterKind.Type)) continue;
            var type = args.Type(name);
            if (modifier == "type")
            {
                values[placeholder] = TypeArgument(type);
                continue;
            }

            if (w == null || context == null) throw new CodegenException($"{{{{{placeholder}}}}} can only be used in the read and write code of a native type");
            // The function runs outside the struct's code, so it can't use its fields.
            var functionContext = new Context
            {
                Reader = context.Reader,
                Writer = context.Writer,
                Resolve = field => throw new CodegenException($"The type argument {Describe(type)} can't use fields ({field})"),
            };
            values[placeholder] = modifier == "read" ? ReadFunction(type, w, functionContext) : WriteFunction(type, w, functionContext);
        }
    }

    /// <summary>The type of the values of a native type's type argument in the language (<c>{{T:type}}</c>).</summary>
    protected virtual string TypeArgument(TypeRef type) =>
        throw new CodegenException($"{Generator} doesn't support type arguments of native types");

    /// <summary>
    /// A function that reads a value of a native type's type argument (<c>{{T:read}}</c>), as an expression: e.g. the
    /// name of a local function added to <paramref name="w"/>, or a lambda. Its code reads with
    /// <see cref="Read(TypeRef, CodeWriter, Context)"/> from a reader of its own (<see cref="Context.WithReader"/>).
    /// </summary>
    /// <param name="context">The call site's context; expressions in it can't use fields.</param>
    protected virtual string ReadFunction(TypeRef type, CodeWriter w, Context context) =>
        throw new CodegenException($"{Generator} doesn't support type arguments of native types");

    /// <summary>A function that writes a value of a native type's type argument (<c>{{T:write}}</c>); see <see cref="ReadFunction"/>.</summary>
    protected virtual string WriteFunction(TypeRef type, CodeWriter w, Context context) =>
        throw new CodegenException($"{Generator} doesn't support type arguments of native types");

    /// <summary>
    /// The type argument whose values a native type's values are in the language, when its type template is just
    /// <c>{{T:type}}</c> (e.g. a compressed <c>T</c>); otherwise null.
    /// </summary>
    protected TypeRef? ValueTypeArgument(NativeRef native)
    {
        var (codegen, args) = Native(native);
        var placeholders = NativeTypes.Placeholders(codegen.Type).ToList();
        if (placeholders is not [var placeholder] || codegen.Type.Trim() != $"{{{{{placeholder}}}}}" || !placeholder.EndsWith(":type", StringComparison.Ordinal))
            return null;
        var name = placeholder[..^":type".Length];
        return args.Parameters.Any(p => p.Name == name && p.Kind == NativeParameterKind.Type) ? args.Type(name) : null;
    }

    /// <summary>A fresh local name; numbered per file.</summary>
    protected string Temp(string name) => $"__{name}{_temp++}";

    protected static string Describe(TypeRef type) => TypeExpressions.TryFormat(type) ?? type.ToString();

    /// <summary>The least number of bytes a value takes on the wire, to check counts before allocating.</summary>
    protected int MinSize(TypeRef type) => Unalias(type) switch
    {
        PrimitiveRef primitive => Primitives.MinWireSize(primitive.Kind),
        FixedPointRef fixedPoint => Primitives.MinWireSize(fixedPoint.Base.Kind),
        NamedRef { As: { } wire } => Primitives.MinWireSize(wire.Kind),
        NamedRef named when Definition(named.Name) is EnumDef enumDef => Primitives.MinWireSize(enumDef.Type.Kind),
        NativeRef native when project.Natives.Find(native.Name) is { } nativeType => nativeType.MinSize(Native(native).Args),
        _ => 0,
    };

    protected bool HasEndian(PrimitiveRef primitive) => Primitives.HasEndian(primitive.Kind);

    protected Endian EndianOf(PrimitiveRef primitive) => primitive.Endian ?? project.Defaults.Endian;

    /// <summary>
    /// The name part of the runtime's read and write methods, shared by all runtimes: <c>U8</c>, <c>I16LE</c>,
    /// <c>VarUInt32</c>, <c>UuidBE</c>, ...
    /// </summary>
    protected string Method(PrimitiveRef primitive)
    {
        var endian = HasEndian(primitive) ? EndianOf(primitive) == Endian.Big ? "BE" : "LE" : "";
        return primitive.Kind switch
        {
            PrimitiveKind.Bool => "Bool",
            PrimitiveKind.U8 => "U8",
            PrimitiveKind.I8 => "I8",
            PrimitiveKind.VarUInt32 => "VarUInt32",
            PrimitiveKind.VarUInt64 => "VarUInt64",
            PrimitiveKind.VarInt32 => "VarInt32",
            PrimitiveKind.VarInt64 => "VarInt64",
            PrimitiveKind.ZigZag32 => "ZigZag32",
            PrimitiveKind.ZigZag64 => "ZigZag64",
            PrimitiveKind.Uuid => "Uuid" + endian,
            var kind => kind.ToString().ToUpperInvariant() + endian,
        };
    }

    /// <summary>
    /// The packet registries: one per protocol of the project, or a single one (with a null protocol) over all packets
    /// when the project declares none. Packets are ordered by id; packets sharing an id are reported and the first one
    /// wins. A registry dispatches either integer or text ids (<see cref="IsTextRegistry"/>); with both, the text ids are
    /// reported and left out. Protocols whose name would clash in the generated code are reported and left out.
    /// </summary>
    /// <param name="registryClass">The name of the class holding the registries.</param>
    protected List<(ProtocolDef? Protocol, List<StructDef> Packets)> Registries(string registryClass)
    {
        if (project.Settings.Protocols.Count == 0)
            return [(null, RegistryPackets(project.Packets, null))];

        var registries = new List<(ProtocolDef?, List<StructDef>)>();
        foreach (var protocol in project.Settings.Protocols.DistinctBy(p => p.Name))
        {
            if (protocol.Name == registryClass || project.Find(protocol.Name) != null || !IsIdentifier(protocol.Name))
            {
                Output.Diagnostics.Add(new Diagnostic(Severity.Error,
                    $"Protocol {protocol.Name}: the name can't be used for a registry class (it is the registry's or a definition's name, or not an identifier)")
                {
                    Area = Diagnostic.ProtocolsArea,
                });
                continue;
            }

            registries.Add((protocol, RegistryPackets(project.PacketsOf(protocol), protocol)));
        }

        return registries;
    }

    private List<StructDef> RegistryPackets(IEnumerable<StructDef> candidates, ProtocolDef? protocol)
    {
        var packets = new List<StructDef>();
        var ordered = candidates.Where(p => p.HasId)
            .OrderBy(p => p.Id).ThenBy(p => p.TextId, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        if (ordered.Any(p => p.Id != null) && ordered.Any(p => p.TextId != null))
        {
            Output.Diagnostics.Add(new Diagnostic(Severity.Error,
                $"{(protocol == null ? "The registry" : $"The registry of protocol {protocol.Name}")} can't dispatch both integer and text ids; " +
                $"{string.Join(", ", ordered.Where(p => p.TextId != null).Select(p => p.Name))} are left out")
            {
                Area = Diagnostic.ProtocolsArea,
            });
            ordered.RemoveAll(p => p.TextId != null);
        }

        foreach (var packet in ordered)
        {
            // The registry returns the packets of the generated code; a bound packet's type is an existing one.
            if (!IsBound(packet)) packets.Add(packet);
            else if (protocol == null || protocol == project.Settings.Protocols.FirstOrDefault(p => p.Contains(packet)))
                Output.Diagnostics.Warning($"{packet.Name} is bound to an existing type and left out of the packet registry", packet.Source);
        }

        foreach (var duplicate in packets.GroupBy(p => p.IdText).Where(g => g.Count() > 1))
        {
            var names = string.Join(", ", duplicate.Select(p => p.Name));
            Output.Diagnostics.Add(new Diagnostic(Severity.Warning, protocol == null
                ? $"Packets {names} share id {duplicate.Key}; only the first is dispatched (declare protocols in the project file to dispatch them separately)"
                : $"Packets {names} share id {duplicate.Key} in protocol {protocol.Name}; only the first is dispatched")
            {
                Area = Diagnostic.ProtocolsArea,
            });
        }

        return packets.DistinctBy(p => p.IdText).ToList();
    }

    /// <summary>Whether a registry's packets (see <see cref="Registries"/>) have text ids rather than integer ids.</summary>
    protected static bool IsTextRegistry(IReadOnlyList<StructDef> packets) => packets.Count > 0 && packets[0].TextId != null;

    /// <summary>A sentence for the documentation of a protocol's registry.</summary>
    protected static string ProtocolRemark(ProtocolDef protocol, int packets)
    {
        var count = packets == 1 ? "1 packet" : $"{packets} packets";
        return protocol.Groups.Count == 0 ? $"{count}." : $"{count}, those in the groups {string.Join(", ", protocol.Groups)}.";
    }

    private static bool IsIdentifier(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>The wire type of an integer or enum tag that is stored in the variant itself; null for string and external tags.</summary>
    protected PrimitiveRef? TagWire(VariantDef variant) => variant.Tag switch
    {
        null => null,
        NamedRef named when Definition(named.Name) is EnumDef enumDef => named.As ?? enumDef.Type,
        var tag => project.Unalias(tag) as PrimitiveRef,
    };

    /// <summary>The enum that a variant's tags are members of, or null.</summary>
    protected EnumDef? TagEnum(VariantDef variant) =>
        variant.Tag is NamedRef named && Definition(named.Name) is EnumDef enumDef ? enumDef : null;

    // ------------------------------------------------------------------ structs

    /// <summary>
    /// The fields of a struct read in wire order: fields that aren't stored (<see cref="FieldDef.IsInput"/>) get a local,
    /// conditional fields are read inside <see cref="OpenIf"/>, constants and ranges are checked after reading.
    /// </summary>
    /// <param name="access">Where a field's value goes: a member of the result, or a local.</param>
    protected void ReadFields(StructDef structDef, CodeWriter w, Func<FieldDef, string> access)
    {
        var known = new Dictionary<string, FieldDef>(StringComparer.Ordinal);
        var resolve = ReadScope(known, access);
        foreach (var field in structDef.Fields)
        {
            var context = new Context { Field = field, Resolve = resolve };
            if (!field.IsInput) DeclareLocal(field, access(field), w);
            if (field.If != null) OpenIf(Condition(field.If, context), w);
            var target = access(field);
            Assign(target, Read(field.Type, w, context), w);
            if (field.Const != null) CheckConstant(structDef, field, target, w);
            if (field.Min != null || field.Max != null) CheckRange(structDef, field, target, w);
            if (field.If != null) CloseIf(w);
            known[field.Name] = field;
        }
    }

    /// <summary>
    /// The fields of a struct written in wire order. Computed fields are calculated first (<see cref="DeclareComputed"/>),
    /// so that they can describe later fields, e.g. a count before a list; constants are written as such, and conditional
    /// fields are checked to be present (<see cref="RequirePresent"/>).
    /// </summary>
    /// <param name="access">Where a field's value is: a member of the value being written, or the local of a computed field.</param>
    protected void WriteFields(StructDef structDef, CodeWriter w, Func<FieldDef, string> access)
    {
        var computedDone = new HashSet<string>(StringComparer.Ordinal);
        var resolve = WriteScope(structDef, computedDone, access);
        var context = new Context { Resolve = resolve };
        foreach (var field in structDef.Fields.Where(f => f.Computed != null))
        {
            var code = Expression(field.Computed!, context, out var kind);
            DeclareComputed(field, access(field), code, kind, w);
            computedDone.Add(field.Name);
        }

        foreach (var field in structDef.Fields)
        {
            var fieldContext = new Context { Resolve = resolve, Field = field };
            if (field.If != null) OpenIf(Condition(field.If, fieldContext), w);
            var value = field.Const != null ? Constant(field) : access(field);
            if (field.If != null && field.Const == null) value = RequirePresent(structDef, field, value, w);
            Write(field.Type, value, w, fieldContext);
            if (field.If != null) CloseIf(w);
        }
    }

    /// <summary>Declares the local of a field that is read but not stored (a computed field, a constant that isn't an input).</summary>
    protected abstract void DeclareLocal(FieldDef field, string name, CodeWriter w);

    /// <summary>Starts a block that runs if <paramref name="condition"/> (a bool expression) holds.</summary>
    protected abstract void OpenIf(string condition, CodeWriter w);

    /// <summary>Ends the block of <see cref="OpenIf"/>.</summary>
    protected virtual void CloseIf(CodeWriter w) => w.Close();

    /// <summary>Stores a value in a field or local.</summary>
    protected virtual void Assign(string target, string value, CodeWriter w) => w.Line($"{target} = {value};");

    /// <summary>Checks a field with a <c>const</c> after reading it (<paramref name="value"/>); see <see cref="Constant"/>.</summary>
    protected abstract void CheckConstant(StructDef structDef, FieldDef field, string value, CodeWriter w);

    /// <summary>Checks a field with a <c>min</c> or <c>max</c> after reading it, if the generator validates ranges.</summary>
    protected abstract void CheckRange(StructDef structDef, FieldDef field, string value, CodeWriter w);

    /// <summary>
    /// Declares the local <paramref name="name"/> of a computed field with the value of <paramref name="code"/>, an
    /// expression of <paramref name="kind"/> (a bool, or a 64-bit integer or double) converted to the field's type.
    /// </summary>
    protected abstract void DeclareComputed(FieldDef field, string name, string code, ValueKind kind, CodeWriter w);

    /// <summary>
    /// A conditional field's value inside its condition: fails when it is missing, else returns the value as the field's
    /// type (e.g. in a new local).
    /// </summary>
    protected abstract string RequirePresent(StructDef structDef, FieldDef field, string value, CodeWriter w);

    /// <summary>The value of a field's <c>const</c> as an expression of the field's type.</summary>
    protected abstract string Constant(FieldDef field);

    // ------------------------------------------------------------------ reading values

    /// <summary>
    /// Reads a value: returns an expression of its value, after adding the statements it needs to <paramref name="w"/>.
    /// Aliases are inlined (unless excluded) and native types filled in from their templates; everything else goes to the
    /// hook of its kind.
    /// </summary>
    protected string Read(TypeRef type, CodeWriter w, Context context)
    {
        switch (type)
        {
            case PrimitiveRef primitive:
                return ReadPrimitive(primitive, w, context);
            case StringRef str:
                return ReadString(str, w, context);
            case BytesRef bytes:
                return ReadBytes(bytes, w, context);
            case ArrayRef array:
                return ReadArray(array, w, context);
            case MapRef map:
                return ReadMap(map, w, context);
            case OptionalRef optional:
                return ReadOptional(optional, w, context);
            case SizedRef sized:
                return ReadSized(sized, w, context);
            case FixedPointRef fixedPoint:
                return ReadFixedPoint(fixedPoint, w, context);
            case NativeRef native:
            {
                var (codegen, args) = Native(native);
                return NativeTemplate(ReadWrite(codegen, native.Name).Read, args, new Dictionary<string, string>
                {
                    ["reader"] = context.Reader, ["pass-reader"] = PassReader(context.Reader),
                }, w, context);
            }
            case NamedRef named when Binding(named) is { } bound:
                return NativeTemplate(ReadWrite(bound, named.Name).Read, null, new Dictionary<string, string>
                {
                    ["reader"] = context.Reader, ["pass-reader"] = PassReader(context.Reader),
                }, w, context);
            case NamedRef named:
                return Definition(named.Name) switch
                {
                    AliasDef alias when !IsExcluded(named.Name) => Read(alias.Target, w, context),
                    EnumDef enumDef => ReadEnum(named, enumDef, w, context),
                    VariantDef variant when named.Tag != null => ReadTaggedVariant(named, variant, w, context),
                    var definition => ReadDefinition(definition, w, context),
                };
            default:
                throw new CodegenException($"Unsupported type {type}");
        }
    }

    protected abstract string ReadPrimitive(PrimitiveRef primitive, CodeWriter w, Context context);
    protected abstract string ReadString(StringRef str, CodeWriter w, Context context);
    protected abstract string ReadBytes(BytesRef bytes, CodeWriter w, Context context);
    protected abstract string ReadArray(ArrayRef array, CodeWriter w, Context context);
    protected abstract string ReadMap(MapRef map, CodeWriter w, Context context);
    protected abstract string ReadOptional(OptionalRef optional, CodeWriter w, Context context);

    /// <summary>A value in a sized block: usually a reader over the block, then <c>Read(sized.Inner, ...)</c> with it.</summary>
    protected abstract string ReadSized(SizedRef sized, CodeWriter w, Context context);

    protected abstract string ReadFixedPoint(FixedPointRef fixedPoint, CodeWriter w, Context context);

    /// <summary>An enum value, read as <c>named.As</c> or the enum's own type.</summary>
    protected abstract string ReadEnum(NamedRef named, EnumDef enumDef, CodeWriter w, Context context);

    /// <summary>A variant whose tag is outside it: <c>named.Tag</c> is the expression of the tag.</summary>
    protected abstract string ReadTaggedVariant(NamedRef named, VariantDef variant, CodeWriter w, Context context);

    /// <summary>A struct, bit field, variant with its own tag, or an excluded alias: usually a call of the type's read method.</summary>
    protected abstract string ReadDefinition(TypeDef definition, CodeWriter w, Context context);

    /// <summary>A reader as an argument of a call (<c>{{pass-reader}}</c> in native templates), e.g. by reference.</summary>
    protected virtual string PassReader(string reader) => reader;

    /// <summary>
    /// The number of elements or bytes of a length: a literal for fixed lengths, else a checked count from a prefix or
    /// expression (<see cref="ReadLengthPrefix"/>, <see cref="CheckCount"/>).
    /// </summary>
    /// <param name="minSize">The least bytes an element takes, so that a count can be checked against the remaining bytes.</param>
    /// <param name="max">The largest count allowed (<c>maxItems</c>), or null.</param>
    protected string ReadCount(LengthSpec length, CodeWriter w, Context context, int minSize, int? max)
    {
        string raw;
        switch (length.Kind)
        {
            case LengthKind.Fixed:
                return length.Fixed.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case LengthKind.Default or LengthKind.Prefix:
                raw = ReadLengthPrefix(length.Prefix ?? project.Defaults.Length, context);
                break;
            case LengthKind.Expression:
                raw = Expression(length.Expression!, context, out _);
                break;
            default:
                throw new CodegenException($"Length {length} is not supported here");
        }

        return CheckCount(raw, minSize, max, w, context);
    }

    /// <summary>
    /// The byte size of a padded field (a padded string, or a sized value with a fill): a literal for fixed lengths, else
    /// the length expression (a number of the expression's type).
    /// </summary>
    protected string PaddedSize(LengthSpec length, Context context) => length.Kind switch
    {
        LengthKind.Fixed => length.Fixed.ToString(System.Globalization.CultureInfo.InvariantCulture),
        LengthKind.Expression => Expression(length.Expression!, context, out _),
        _ => throw new CodegenException($"Padding needs a fixed length or a length expression, not {length}"),
    };

    /// <summary>Whether the fill of a sized value is checked strictly (its own <c>check</c>, else the project default).</summary>
    protected bool StrictFill(SizedRef sized) => sized.Fill != null && (sized.Check ?? project.Defaults.Check) == PaddingCheck.Strict;

    /// <summary>The fill pattern of a sized value as hex byte literals: <c>0x00</c>, <c>0xde, 0xad</c>.</summary>
    protected static string FillBytes(SizedRef sized) =>
        string.Join(", ", Enumerable.Range(0, sized.Fill!.Length / 2).Select(i => "0x" + sized.Fill.Substring(i * 2, 2)));

    /// <summary>
    /// The most bytes a string with a <see cref="StringRef.Max"/> can take, to reject a longer count before decoding; null
    /// without a max.
    /// </summary>
    protected int? StringByteLimit(StringRef str) => str.Max is { } max
        ? (int)Math.Min(int.MaxValue, (long)max * Simulation.Strings.MaxBytesPerUnit(str.Encoding ?? project.Defaults.String))
        : null;

    /// <summary>Whether a padded string's padding is checked strictly (its own <c>check</c>, else the project default).</summary>
    protected bool StrictPadding(StringRef str) => str.Padding != StringPadding.None && (str.Check ?? project.Defaults.Check) == PaddingCheck.Strict;

    /// <summary>
    /// The pad byte of a padded string, for generators that write single-byte pad characters: ASCII, or Latin-1 for
    /// Latin-1 strings.
    /// </summary>
    protected int PadByte(StringRef str) => str.Pad <= ((str.Encoding ?? project.Defaults.String) == StringEncoding.Latin1 ? 0xFF : 0x7F)
        ? str.Pad
        : throw new CodegenException($"The pad character of {str} must be one byte in its encoding");

    /// <summary>An expression that reads a length prefix.</summary>
    protected abstract string ReadLengthPrefix(PrimitiveRef prefix, Context context);

    /// <summary>
    /// Checks a count read from the wire (not negative, not more than <paramref name="max"/>, not more elements of
    /// <paramref name="minSize"/> than bytes remain) into a new local, and returns its name.
    /// </summary>
    protected abstract string CheckCount(string raw, int minSize, int? max, CodeWriter w, Context context);

    // ------------------------------------------------------------------ writing values

    /// <summary>
    /// Writes a value (an expression of its type): aliases are inlined (unless excluded) and native types filled in from
    /// their templates; everything else goes to the hook of its kind.
    /// </summary>
    protected void Write(TypeRef type, string value, CodeWriter w, Context context)
    {
        switch (type)
        {
            case PrimitiveRef primitive:
                WritePrimitive(primitive, value, w, context);
                break;
            case StringRef str:
                WriteString(str, value, w, context);
                break;
            case BytesRef bytes:
                WriteBytes(bytes, value, w, context);
                break;
            case ArrayRef array:
                WriteArray(array, value, w, context);
                break;
            case MapRef map:
                WriteMap(map, value, w, context);
                break;
            case OptionalRef optional:
                WriteOptional(optional, value, w, context);
                break;
            case SizedRef sized:
                WriteSized(sized, value, w, context);
                break;
            case FixedPointRef fixedPoint:
                WriteFixedPoint(fixedPoint, value, w, context);
                break;
            case NativeRef native:
            {
                var (codegen, args) = Native(native);
                w.Line(Statement(NativeTemplate(ReadWrite(codegen, native.Name).Write, args, new Dictionary<string, string>
                {
                    ["writer"] = context.Writer, ["pass-writer"] = PassWriter(context.Writer), ["value"] = value,
                }, w, context)));
                break;
            }
            case NamedRef named when Binding(named) is { } bound:
                w.Line(Statement(NativeTemplate(ReadWrite(bound, named.Name).Write, null, new Dictionary<string, string>
                {
                    ["writer"] = context.Writer, ["pass-writer"] = PassWriter(context.Writer), ["value"] = value,
                }, w, context)));
                break;
            case NamedRef named:
                switch (Definition(named.Name))
                {
                    case AliasDef alias when !IsExcluded(named.Name):
                        Write(alias.Target, value, w, context);
                        break;
                    case EnumDef enumDef:
                        WriteEnum(named, enumDef, value, w, context);
                        break;
                    case VariantDef variant when named.Tag != null:
                        WriteTaggedVariant(named, variant, value, w, context);
                        break;
                    case var definition:
                        WriteDefinition(definition, value, w, context);
                        break;
                }

                break;
            default:
                throw new CodegenException($"Unsupported type {type}");
        }
    }

    protected abstract void WritePrimitive(PrimitiveRef primitive, string value, CodeWriter w, Context context);
    protected abstract void WriteString(StringRef str, string value, CodeWriter w, Context context);
    protected abstract void WriteBytes(BytesRef bytes, string value, CodeWriter w, Context context);
    protected abstract void WriteArray(ArrayRef array, string value, CodeWriter w, Context context);
    protected abstract void WriteMap(MapRef map, string value, CodeWriter w, Context context);
    protected abstract void WriteOptional(OptionalRef optional, string value, CodeWriter w, Context context);

    /// <summary>A value in a sized block: usually written to a buffer, then the length and the buffer.</summary>
    protected abstract void WriteSized(SizedRef sized, string value, CodeWriter w, Context context);

    protected abstract void WriteFixedPoint(FixedPointRef fixedPoint, string value, CodeWriter w, Context context);
    protected abstract void WriteEnum(NamedRef named, EnumDef enumDef, string value, CodeWriter w, Context context);

    /// <summary>A variant whose tag is outside it: only its content, the tag is written as a field of its own.</summary>
    protected abstract void WriteTaggedVariant(NamedRef named, VariantDef variant, string value, CodeWriter w, Context context);

    /// <summary>A struct, bit field, variant with its own tag, or an excluded alias: usually a call of the value's write method.</summary>
    protected abstract void WriteDefinition(TypeDef definition, string value, CodeWriter w, Context context);

    /// <summary>A writer as an argument of a call (<c>{{pass-writer}}</c> in native templates), e.g. by reference.</summary>
    protected virtual string PassWriter(string writer) => writer;

    /// <summary>An expression as a statement.</summary>
    protected virtual string Statement(string expression) => expression + ";";

    /// <summary>
    /// Writes the length of a value whose number of elements or bytes is <paramref name="count"/>: the prefix
    /// (<see cref="WriteLengthPrefix"/>), or a check of a fixed length (<see cref="CheckFixedCount"/>). Lengths given by
    /// an expression or the end of the data aren't written.
    /// </summary>
    /// <param name="what">The field, for messages.</param>
    protected void WriteCount(LengthSpec length, string count, CodeWriter w, Context context, string what)
    {
        switch (length.Kind)
        {
            case LengthKind.Default or LengthKind.Prefix:
                WriteLengthPrefix(length.Prefix ?? project.Defaults.Length, count, w, context);
                break;
            case LengthKind.Fixed:
                CheckFixedCount(count, length.Fixed, what, w, context);
                break;
        }
    }

    /// <summary>Writes a length prefix; <paramref name="count"/> is an int expression.</summary>
    protected abstract void WriteLengthPrefix(PrimitiveRef prefix, string count, CodeWriter w, Context context);

    /// <summary>Fails when <paramref name="count"/> isn't the fixed length.</summary>
    protected abstract void CheckFixedCount(string count, int length, string what, CodeWriter w, Context context);

    // ------------------------------------------------------------------ cycles

    /// <summary>Types that contain themselves by value (directly or through other types), by qualified name.</summary>
    protected HashSet<string> SelfContainingTypes()
    {
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var definition in Generated)
        {
            var targets = new List<string>();
            switch (definition)
            {
                case StructDef structDef:
                    foreach (var field in structDef.Fields) targets.AddRange(ByValue(field.Type));
                    break;
                case VariantDef variant:
                    foreach (var variantCase in variant.Cases.Where(c => c.Type != null)) targets.AddRange(ByValue(variantCase.Type!));
                    break;
            }

            edges[definition.QualifiedName] = targets;
        }

        // Tarjan's strongly connected components.
        var index = 0;
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);

        void Connect(string node)
        {
            indices[node] = lowLinks[node] = index++;
            stack.Push(node);
            onStack.Add(node);
            foreach (var next in edges.GetValueOrDefault(node) ?? [])
            {
                if (!edges.ContainsKey(next)) continue;
                if (!indices.ContainsKey(next))
                {
                    Connect(next);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[next]);
                }
                else if (onStack.Contains(next))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indices[next]);
                }
            }

            if (lowLinks[node] != indices[node]) return;
            var component = new List<string>();
            string member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            } while (member != node);

            if (component.Count > 1 || edges[node].Contains(node)) result.UnionWith(component);
        }

        foreach (var node in edges.Keys.Order(StringComparer.Ordinal))
            if (!indices.ContainsKey(node)) Connect(node);
        return result;
    }

    private IEnumerable<string> ByValue(TypeRef type)
    {
        switch (type)
        {
            case OptionalRef optional:
                return ByValue(optional.Inner);
            case SizedRef sized:
                return ByValue(sized.Inner);
            case NativeRef native:
                try
                {
                    return ValueTypeArgument(native) is { } inner ? ByValue(inner) : [];
                }
                catch (CodegenException)
                {
                    return [];
                }
            case NamedRef named when project.Find(named.Name) is AliasDef alias:
                return ByValue(alias.Target).Append(alias.QualifiedName);
            case NamedRef named:
                return project.Find(named.Name) is { } target ? [target.QualifiedName] : [];
            default:
                return [];
        }
    }

    // ------------------------------------------------------------------ expressions

    /// <summary>Where code is generated: the names of the reader and writer, and the names expressions can use.</summary>
    public sealed class Context
    {
        public string Reader { get; init; } = "reader";
        public string Writer { get; init; } = "writer";
        public required Func<string, Value?> Resolve { get; init; }
        public FieldDef? Field { get; init; }

        /// <summary>The same names and streams, for the elements of a collection (no field).</summary>
        public Context Element() => new() { Reader = Reader, Writer = Writer, Resolve = Resolve };

        public Context WithReader(string reader) => new() { Reader = reader, Writer = Writer, Resolve = Resolve, Field = Field };

        public Context WithWriter(string writer) => new() { Reader = Reader, Writer = writer, Resolve = Resolve, Field = Field };

        public static Context Empty => new() { Resolve = _ => null };
    }

    /// <summary>What an expression evaluates to.</summary>
    public enum ValueKind
    {
        Bool,
        Number,
        Float,
        Text,
        Null,
        Other,
    }

    /// <param name="Code">The value converted for arithmetic (a 64-bit integer, a double or a bool).</param>
    /// <param name="Raw">The plain access, for null checks, members and len().</param>
    /// <param name="MayBeNull">Optional or conditional: len() and member access must check for null.</param>
    public sealed record Value(string Code, string Raw, ValueKind Kind, TypeRef? Type, bool MayBeNull = false);

    /// <summary>The name of a field or case in the generated type.</summary>
    protected abstract string MemberName(string name, string owner);

    /// <summary>An integer literal of the 64-bit type expressions are evaluated in.</summary>
    protected abstract string NumberLiteral(decimal value);

    protected abstract string StringLiteral(string text);

    protected abstract Value EnumConstant(EnumDef enumDef, string member);

    /// <summary>
    /// Converts a field access for arithmetic: integers and enums to a 64-bit integer, floats to double. A value that may
    /// be null counts as 0 (or false).
    /// </summary>
    /// <param name="declared">The type of the value (the inner type of an optional).</param>
    protected abstract string Convert(string access, ValueKind kind, TypeRef declared, bool mayBeNull);

    protected abstract string IndexAccess(string target, string index);

    /// <summary><c>len(x)</c> as a 64-bit integer; null counts as empty.</summary>
    protected abstract string Length(Value value);

    /// <summary><c>bytelen(x)</c>: the UTF-8 length of a string as a 64-bit integer.</summary>
    protected abstract string ByteLength(Value value);

    /// <summary><c>tagof(x)</c> of a variant with integer or enum tags.</summary>
    protected abstract string TagNumber(Value value);

    /// <summary><c>tagof(x)</c> of a variant with string tags.</summary>
    protected abstract string TagText(VariantDef variant, Value value);

    /// <summary>Text (in)equality, <paramref name="op"/> is <c>==</c> or <c>!=</c>.</summary>
    protected abstract string TextCompare(string left, string right, string op);

    /// <param name="conditional">The field has a condition, so it may be missing.</param>
    protected Value FieldValue(string access, TypeRef type, bool conditional)
    {
        var resolved = Unalias(type);
        var declared = type;
        var mayBeNull = conditional;
        if (resolved is OptionalRef optional)
        {
            declared = optional.Inner;
            resolved = Unalias(optional.Inner);
            mayBeNull = true;
        }

        if (resolved is NamedRef boundRef && Binding(boundRef) is { } bound)
            return BoundValue(access, boundRef, bound, type, mayBeNull);

        var kind = resolved switch
        {
            PrimitiveRef { Kind: PrimitiveKind.Bool } => ValueKind.Bool,
            PrimitiveRef { Kind: PrimitiveKind.F16 or PrimitiveKind.F32 or PrimitiveKind.F64 } or FixedPointRef => ValueKind.Float,
            PrimitiveRef { Kind: PrimitiveKind.Uuid } => ValueKind.Other,
            PrimitiveRef => ValueKind.Number,
            NamedRef named when Definition(named.Name) is EnumDef => ValueKind.Number,
            StringRef => ValueKind.Text,
            _ => ValueKind.Other,
        };
        var code = kind is ValueKind.Bool or ValueKind.Number or ValueKind.Float ? Convert(access, kind, declared, mayBeNull) : access;
        return new Value(code, access, kind, type, mayBeNull);
    }

    /// <summary>
    /// A value of a bound type in an expression: a bound enum with a <see cref="NativeCodegen.Value"/> template is its wire
    /// value; other values can only be compared with null.
    /// </summary>
    private Value BoundValue(string access, NamedRef named, NativeCodegen bound, TypeRef type, bool mayBeNull)
    {
        if (bound.Value == null || Definition(named.Name) is not EnumDef enumDef) return new Value(access, access, ValueKind.Other, type, mayBeNull);
        if (mayBeNull) throw new CodegenException($"Expressions can't use an optional or conditional {named.Name}, which is bound to an existing type");
        var number = NativeTemplate(bound.Value, null, new Dictionary<string, string> { ["value"] = access });
        return new Value(Convert(number, ValueKind.Number, enumDef.Type, false), access, ValueKind.Number, type);
    }

    /// <summary>The bound definition a value is of, for the error of an expression that needs to look into it; null when it is generated.</summary>
    private string? BoundName(Value value) =>
        value.Type != null && Unalias(value.Type) is var resolved && (resolved is OptionalRef optional ? Unalias(optional.Inner) : resolved) is NamedRef named
        && IsBound(Definition(named.Name))
            ? named.Name
            : null;

    protected Value Translate(Expr expression, Context context)
    {
        switch (expression)
        {
            case LiteralExpr { Value: null }:
                return new Value("null", "null", ValueKind.Null, null);
            case LiteralExpr { Value: bool b }:
                return new Value(b ? "true" : "false", b ? "true" : "false", ValueKind.Bool, null);
            case LiteralExpr { Value: decimal d }:
                var number = NumberLiteral(d);
                return new Value(number, number, ValueKind.Number, null);
            case LiteralExpr { Value: double f }:
                var text = f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
                return new Value(text, text, ValueKind.Float, null);
            case LiteralExpr { Value: string s }:
                return new Value(StringLiteral(s), StringLiteral(s), ValueKind.Text, null);
            case NameExpr { Name: "_parent" or "_root" }:
                throw new CodegenException("_parent and _root are not supported by the generators");
            case NameExpr name:
                return context.Resolve(name.Name) ?? throw new CodegenException($"Unknown name '{name.Name}' in an expression");
            case EnumConstExpr constant:
                if (Definition(constant.Enum) is not EnumDef enumDef || enumDef.FindByName(constant.Member) is not { } member)
                    throw new CodegenException($"Unknown enum constant {constant}");
                if (IsBound(enumDef))
                {
                    // Bound enums are compared by their wire values (see BoundValue).
                    var wire = NumberLiteral(member.Value);
                    return new Value(wire, wire, ValueKind.Number, null);
                }

                return EnumConstant(enumDef, constant.Member);
            case MemberExpr memberExpr:
            {
                var target = Translate(memberExpr.Target, context);
                if (BoundName(target) is { } boundName)
                    throw new CodegenException($"Can't access '{memberExpr.Name}' in {memberExpr}: {boundName} is bound to an existing type");
                if (target.Type == null || Unalias(target.Type) is not NamedRef named || Definition(named.Name) is not StructDef structDef)
                    throw new CodegenException($"Can't access '{memberExpr.Name}' in {memberExpr}");
                var field = structDef.Fields.FirstOrDefault(f => f.Name == memberExpr.Name && f.IsInput)
                    ?? throw new CodegenException($"{structDef.Name} has no field '{memberExpr.Name}'");
                return FieldValue($"{target.Raw}.{MemberName(field.Name, structDef.Name)}", field.Type, field.If != null);
            }
            case IndexExpr indexExpr:
            {
                var target = Translate(indexExpr.Target, context);
                var index = Translate(indexExpr.Index, context);
                if (target.Type == null || Unalias(target.Type) is not ArrayRef array)
                    throw new CodegenException($"Can't index {indexExpr.Target}");
                return FieldValue(IndexAccess(target.Raw, $"(int){index.Code}"), array.Element, false);
            }
            case UnaryExpr unary:
            {
                var operand = Translate(unary.Operand, context);
                return unary.Operator switch
                {
                    "!" => new Value($"!{Bool(operand)}", "", ValueKind.Bool, null),
                    "-" => new Value($"(-{operand.Code})", "", operand.Kind == ValueKind.Float ? ValueKind.Float : ValueKind.Number, null),
                    _ => new Value($"(~{operand.Code})", "", ValueKind.Number, null),
                };
            }
            case BinaryExpr binary:
                return Binary(binary, context);
            case ConditionalExpr conditional:
            {
                var then = Translate(conditional.Then, context);
                var otherwise = Translate(conditional.Else, context);
                var code = $"({Bool(Translate(conditional.Condition, context))} ? {then.Code} : {otherwise.Code})";
                return new Value(code, code, then.Kind == ValueKind.Null ? otherwise.Kind : then.Kind, then.Type ?? otherwise.Type);
            }
            case CallExpr call:
            {
                if (call.Arguments.Count != 1) throw new CodegenException($"{call.Function}() takes one argument");
                var argument = Translate(call.Arguments[0], context);
                if (BoundName(argument) is { } boundArgument)
                    throw new CodegenException($"{call.Function}() can't be used on {boundArgument}, which is bound to an existing type");
                if (call.Function == "tagof" && argument.Type != null && Unalias(argument.Type) is NamedRef tagged
                    && Definition(tagged.Name) is VariantDef taggedVariant && project.TagKind(taggedVariant) == VariantTagKind.String)
                {
                    var tagText = TagText(taggedVariant, argument);
                    return new Value(tagText, tagText, ValueKind.Text, null);
                }

                var code = call.Function switch
                {
                    "len" => Length(argument),
                    "bytelen" => ByteLength(argument),
                    "tagof" => TagNumber(argument),
                    _ => throw new CodegenException($"Unknown function {call.Function}()"),
                };
                return new Value(code, code, ValueKind.Number, null);
            }
            default:
                throw new CodegenException($"Unsupported expression {expression}");
        }
    }

    private Value Binary(BinaryExpr binary, Context context)
    {
        var left = Translate(binary.Left, context);
        var right = Translate(binary.Right, context);
        var op = binary.Operator;
        switch (op)
        {
            case "==" or "!=" when left.Kind == ValueKind.Null || right.Kind == ValueKind.Null:
                return new Value($"({left.Raw} {op} {right.Raw})", "", ValueKind.Bool, null);
            case "==" or "!=" when left.Kind == ValueKind.Bool && right.Kind == ValueKind.Bool:
            case "&" or "|" or "^" when left.Kind == ValueKind.Bool && right.Kind == ValueKind.Bool:
                return new Value($"({left.Code} {op} {right.Code})", "", ValueKind.Bool, null);
            case "==" or "!=" when left.Kind == ValueKind.Text || right.Kind == ValueKind.Text:
                return new Value(TextCompare(left.Raw, right.Raw, op), "", ValueKind.Bool, null);
            case "&&" or "||":
                return new Value($"({Bool(left)} {op} {Bool(right)})", "", ValueKind.Bool, null);
            case "==" or "!=" or "<" or "<=" or ">" or ">=":
                return new Value($"({Numeric(left)} {op} {Numeric(right)})", "", ValueKind.Bool, null);
            case "<<" or ">>":
                return new Value($"({Numeric(left)} {op} (int){Numeric(right)})", "", ValueKind.Number, null);
            case "+" when left.Kind == ValueKind.Text || right.Kind == ValueKind.Text:
                return new Value($"({left.Raw} + {right.Raw})", "", ValueKind.Text, null);
            default:
                var kind = left.Kind == ValueKind.Float || right.Kind == ValueKind.Float ? ValueKind.Float : ValueKind.Number;
                if (kind == ValueKind.Float && op is "&" or "|" or "^") throw new CodegenException($"Operator {op} needs integers");
                var code = $"({Numeric(left)} {op} {Numeric(right)})";
                return new Value(code, code, kind, null);
        }
    }

    protected static string Bool(Value value) => value.Kind switch
    {
        ValueKind.Bool => value.Code,
        ValueKind.Number or ValueKind.Float => $"({value.Code} != 0)",
        ValueKind.Null => "false",
        _ => $"({value.Raw} != null)",
    };

    protected static string Numeric(Value value) => value.Kind == ValueKind.Bool ? $"({value.Code} ? 1L : 0L)" : value.Code;

    /// <summary>An expression as a bool, or as a 64-bit integer or double (see <paramref name="kind"/>).</summary>
    protected string Expression(string text, Context context, out ValueKind kind)
    {
        var value = Translate(Expr.Parse(text), context);
        kind = value.Kind;
        return value.Kind == ValueKind.Bool ? value.Code : Numeric(value);
    }

    protected string Condition(string text, Context context) => Bool(Translate(Expr.Parse(text), context));

    // ------------------------------------------------------------------ scopes

    /// <summary>Names while reading a struct: the fields read so far.</summary>
    protected Func<string, Value?> ReadScope(IReadOnlyDictionary<string, FieldDef> known, Func<FieldDef, string> access) =>
        name => known.TryGetValue(name, out var field) ? FieldValue(access(field), field.Type, field.If != null) : null;

    /// <summary>
    /// Names while writing a struct: stored fields, constants and the computed fields that are calculated already.
    /// </summary>
    protected Func<string, Value?> WriteScope(StructDef structDef, IReadOnlySet<string> computedDone, Func<FieldDef, string> access)
    {
        var fields = structDef.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        return name =>
        {
            if (!fields.TryGetValue(name, out var field)) return null;
            if (field.Const != null) return FieldValue($"({Constant(field)})", field.Type, false);
            if (field.Computed != null && !computedDone.Contains(name))
                throw new CodegenException($"Computed field '{name}' is used before it is calculated");
            return FieldValue(access(field), field.Type, field.If != null);
        };
    }

    /// <summary>Names in the end condition of an <c>until</c> array are the fields of the element.</summary>
    protected Context ElementContext(ArrayRef array, string item)
    {
        if (Unalias(array.Element) is not NamedRef named || Definition(named.Name) is not StructDef structDef)
            throw new CodegenException($"An array with 'until' needs struct elements, not {Describe(array.Element)}");
        if (IsBound(structDef))
            throw new CodegenException($"An array with 'until' can't end on elements of {named.Name}, which is bound to an existing type");
        return new Context
        {
            Resolve = n => structDef.Fields.FirstOrDefault(f => f.Name == n) switch
            {
                null => null,
                { IsInput: false } => throw new CodegenException($"The until condition of {structDef.Name}[] uses '{n}', which is not stored in {structDef.Name}"),
                var f => FieldValue($"{item}.{MemberName(f.Name, structDef.Name)}", f.Type, f.If != null),
            },
        };
    }
}
