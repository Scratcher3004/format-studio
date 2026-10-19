using FormatStudio.Dsl;
using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Natives;
using FormatStudio.Projects;
using FormatStudio.Simulation;

namespace FormatStudio.Validation;

/// <summary>Checks references, expressions and tags of all definitions in a project.</summary>
public sealed class Validator
{
    private static readonly HashSet<string> ScopeNames = ["_parent", "_root"];

    private readonly FormatProject _project;
    private readonly DiagnosticBag _diagnostics;

    private Validator(FormatProject project, DiagnosticBag diagnostics)
    {
        _project = project;
        _diagnostics = diagnostics;
    }

    /// <summary>Validates the whole project and adds the findings to <see cref="FormatProject.Diagnostics"/>.</summary>
    public static void Validate(FormatProject project)
    {
        var validator = new Validator(project, project.Diagnostics);
        foreach (var definition in project.Types.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            validator.Check(definition);
        validator.CheckPacketIds();
        validator.CheckAliasCycles();
        validator.CheckNativeDefaults();
        validator.CheckLibrarySettings();
        validator.CheckTests();
    }

    /// <summary>Validates one definition against the project, e.g. while editing.</summary>
    public static DiagnosticBag Validate(FormatProject project, TypeDef definition)
    {
        var diagnostics = new DiagnosticBag();
        new Validator(project, diagnostics).Check(definition);
        return diagnostics;
    }

    private void Check(TypeDef definition)
    {
        if (_project.Natives.Find(definition.Name) != null)
            _diagnostics.Error($"{definition.Name} is the name of a native type, which references to it would mean; rename the definition", definition.Source);
        if (definition.SkipCodegen is { All: false } skip)
        {
            foreach (var name in skip.Targets.Where(n => _project.Generators.Find(n) == null && _project.Settings.Codegen.All(t => t.Name != n)))
                _diagnostics.Warning($"{definition.Name}: skipCodegen names '{name}', which is neither a codegen target nor a generator", definition.Source);
        }

        switch (definition)
        {
            case StructDef structDef:
                CheckStruct(structDef);
                break;
            case EnumDef enumDef:
                foreach (var duplicate in enumDef.Members.GroupBy(m => m.Value).Where(g => g.Count() > 1 && !enumDef.Flags))
                {
                    _diagnostics.Info($"Enum {enumDef.Name}: members {string.Join(", ", duplicate.Select(m => m.Name))} share value {duplicate.Key}",
                        enumDef.Source);
                }

                var (min, max) = Primitives.Range(enumDef.Type.Kind);
                foreach (var member in enumDef.Members.Where(m => m.Value < min || m.Value > max))
                    _diagnostics.Error($"Enum {enumDef.Name}: {member.Name} = {member.Value} doesn't fit {enumDef.Type}", enumDef.Source);
                break;
            case VariantDef variant:
                CheckVariant(variant);
                break;
            case AliasDef alias:
                CheckType(alias.Target, alias.Source, $"{alias.Name}", null);
                break;
            case BitfieldDef bitfield:
                foreach (var member in bitfield.Members.Where(m => m.Type != null && m.Type != "bool"))
                {
                    if (_project.Find(member.Type!) is not EnumDef)
                        _diagnostics.Error($"Bitfield {bitfield.Name}.{member.Name}: '{member.Type}' is not bool or an enum", bitfield.Source);
                }

                foreach (var duplicate in bitfield.Members.GroupBy(m => m.Name).Where(g => g.Count() > 1))
                    _diagnostics.Error($"Bitfield {bitfield.Name}: duplicate member '{duplicate.Key}'", bitfield.Source);
                break;
        }
    }

    /// <summary>
    /// Problems that make a test case fail whatever the definitions: its type, its hex or file, missing data, unknown keys,
    /// expectations it can't meet. A skipped case's type isn't checked, as it may be one the project doesn't have yet.
    /// </summary>
    private void CheckTests()
    {
        foreach (var test in _project.Tests)
        {
            var source = test.Source;
            var name = $"Test {test.Name}";
            foreach (var key in test.UnknownKeys)
            {
                var keys = key.Split('.')[0] switch
                {
                    "expect" when key.Contains('.') => TestExpectation.Keys,
                    "packet" when key.Contains('.') => PacketFrame.Keys,
                    "fuzz" when key.Contains('.') => FuzzOptions.Keys,
                    _ => TestFiles.Keys,
                };
                _diagnostics.Warning($"{name}: unknown key '{key}' ({string.Join(", ", keys)})", source);
            }

            if (test.Hex != null && test.File != null) _diagnostics.Error($"{name} has both a 'hex' and a 'file'; keep one", source);
            else if (test.Fuzz != null && (test.Value != null || test.HasBytes || test.Expect != null))
                _diagnostics.Error($"{name}: a fuzz case makes up its values, so it has no 'value', 'hex', 'file' or 'expect'", source);
            else if (test.Value == null && !test.HasBytes && test.Fuzz == null) _diagnostics.Error($"{name} needs a 'value', 'hex' or 'file'", source);
            if (test.Packet != null && test.Type == null && test.Value != null)
                _diagnostics.Error($"{name}: a packet case with a 'value' needs a 'type', the packet to encode it as", source);
            if (test.Expect is { ExpectsFailure: true } expect)
            {
                if (expect.Fails == ExpectedFailure.Encode && test.Value == null) _diagnostics.Error($"{name}: 'fails: encode' needs a 'value'", source);
                if (expect.Fails == ExpectedFailure.Decode && !test.HasBytes) _diagnostics.Error($"{name}: 'fails: decode' needs a 'hex' or 'file'", source);
                if (expect.HasValue || expect.Warnings != null || expect.Consumed != null)
                    _diagnostics.Warning($"{name}: a case that must fail only checks its error; 'value', 'warnings' and 'consumed' are ignored", source);
            }

            foreach (var target in test.SkipTargets.Keys.Where(t => _project.Settings.Codegen.All(c => c.Name != t) && _project.Generators.Find(t) == null))
                _diagnostics.Warning($"{name}: 'skip-targets' names '{target}', which is neither a codegen target nor a generator", source);
            if (test.HexAuto && test.Value == null)
                _diagnostics.Error($"{name}: 'hex: auto' is filled in from the value's encoding, so the case needs a 'value'", source);
            if (test.Hex != null && !test.HexAuto)
            {
                try
                {
                    Hex.Parse(test.Hex);
                }
                catch (System.FormatException e)
                {
                    _diagnostics.Error($"{name}: invalid hex: {e.Message}", source);
                }
            }

            if (test.File != null)
            {
                try
                {
                    if (!File.Exists(_project.TestFilePath(test))) _diagnostics.Error($"{name}: there is no file {test.File}", source);
                }
                catch (FormatDefinitionException e)
                {
                    _diagnostics.Error($"{name}: {e.Message}", source);
                }
            }

            if (test.Skip != null) continue;
            ProtocolDef? protocol = null;
            if (test.Packet?.Protocol is { } protocolName && (protocol = _project.FindProtocol(protocolName)) == null)
                _diagnostics.Error($"{name}: there is no protocol '{protocolName}'", source);
            if (test.Type == null) continue;
            TypeRef type;
            try
            {
                type = _project.Normalize(TypeExpressions.Parse(test.Type));
            }
            catch (System.FormatException e)
            {
                _diagnostics.Error($"{name}: {e.Message}", source);
                continue;
            }

            CheckType(type, source, test.Name, null);
            if (test.Packet is not { } frame) continue;
            if (type is not NamedRef named || _project.Find(named.Name) is not StructDef { IsPacket: true } packet)
                _diagnostics.Error($"{name}: {test.Type} is not a packet, so its bytes can't start with a packet id", source);
            else if (protocol != null && !protocol.Contains(packet) || frame.Group != null && !packet.Groups.Contains(frame.Group))
                _diagnostics.Error($"{name}: {packet.Name} is not one of the packets the id is looked up among ({FrameText(frame)})", source);
        }

        foreach (var duplicate in _project.Tests.GroupBy(t => (t.Source?.File, t.Name)).Where(g => g.Count() > 1))
            _diagnostics.Warning($"Several tests are named {duplicate.Key.Name}", duplicate.Skip(1).First().Source);
    }

    private static string FrameText(PacketFrame frame) =>
        string.Join(", ", new[] { frame.Protocol is { } p ? "protocol " + p : null, frame.Group is { } g ? "group " + g : null }.OfType<string>());

    private void CheckStruct(StructDef structDef)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var all = structDef.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in structDef.Fields)
        {
            var path = $"{structDef.Name}.{field.Name}";
            var source = field.Source ?? structDef.Source;
            if (!names.Add(field.Name)) _diagnostics.Error($"Duplicate field '{field.Name}'", source, path);

            // Conditions and lengths are evaluated while reading, so they can only use earlier fields.
            CheckExpression(field.If, names, field.Name, source, path, "if");
            CheckExpression(field.Computed, all, null, source, path, "computed");
            CheckType(field.Type, source, path, names);
            if (field.Const != null && field.If != null)
                _diagnostics.Warning("A conditional const field", source, path);
        }
    }

    private void CheckExpression(string? text, IReadOnlySet<string> known, string? self, SourceLocation? source, string path,
        string what)
    {
        if (text == null) return;
        if (!Expr.TryParse(text, out var expression, out var error))
        {
            _diagnostics.Error($"Invalid {what} expression: {error}", source, path);
            return;
        }

        foreach (var name in expression!.Names().Distinct())
        {
            if (ScopeNames.Contains(name) || known.Contains(name) && name != self) continue;
            _diagnostics.Error($"The {what} expression uses '{name}', which is not an earlier field", source, path);
        }

        foreach (var constant in Constants(expression))
        {
            if (_project.Find(constant.Enum) is not EnumDef enumDef || enumDef.FindByName(constant.Member) == null)
                _diagnostics.Error($"Unknown enum constant {constant}", source, path);
        }
    }

    private static IEnumerable<EnumConstExpr> Constants(Expr expression) => expression switch
    {
        EnumConstExpr constant => [constant],
        MemberExpr member => Constants(member.Target),
        IndexExpr index => Constants(index.Target).Concat(Constants(index.Index)),
        UnaryExpr unary => Constants(unary.Operand),
        BinaryExpr binary => Constants(binary.Left).Concat(Constants(binary.Right)),
        ConditionalExpr conditional => Constants(conditional.Condition).Concat(Constants(conditional.Then)).Concat(Constants(conditional.Else)),
        CallExpr call => call.Arguments.SelectMany(Constants),
        _ => [],
    };

    /// <param name="earlier">Fields usable in length and tag expressions, or null outside structs.</param>
    private void CheckType(TypeRef type, SourceLocation? source, string path, IReadOnlySet<string>? earlier)
    {
        switch (type)
        {
            case NamedRef named:
                var (target, error) = _project.Resolve(named.Name);
                if (target == null)
                {
                    _diagnostics.Error(error!, source, path);
                    return;
                }

                if (named.As != null)
                {
                    if (target is not EnumDef) _diagnostics.Error($"'as' only applies to enums, not to {target.Kind} {target.Name}", source, path);
                    else if (!Primitives.IsInteger(named.As.Kind)) _diagnostics.Error($"An enum can't be stored as {named.As}", source, path);
                }

                if (named.Tag != null)
                {
                    if (target is not VariantDef) _diagnostics.Error($"'tag' only applies to variants, not to {target.Kind} {target.Name}", source, path);
                    else if (earlier == null) _diagnostics.Error("An external variant tag can only be used in a struct field", source, path);
                    else CheckExpression(named.Tag, earlier, null, source, path, "tag");
                }
                else if (target is VariantDef { Tag: null })
                {
                    _diagnostics.Error($"Variant {target.Name} has no tag of its own; the use site needs 'tag'", source, path);
                }

                break;
            case ArrayRef { Length.Kind: LengthKind.Until } array:
                CheckUntil(array, source, path);
                CheckType(array.Element, source, path + "[]", null);
                break;
            case ArrayRef array:
                CheckLength(array.Length, source, path, earlier, allowZero: false);
                CheckType(array.Element, source, path + "[]", null);
                break;
            case MapRef map:
                CheckLength(map.Length, source, path, earlier, allowZero: false);
                CheckType(map.Key, source, path + ".key", null);
                CheckType(map.Value, source, path + ".value", null);
                break;
            case StringRef str:
                CheckLength(str.Length, source, path, earlier, allowZero: true);
                break;
            case BytesRef bytes:
                CheckLength(bytes.Length, source, path, earlier, allowZero: false);
                break;
            case SizedRef sized:
                CheckLength(sized.Length, source, path, earlier, allowZero: false);
                if (sized.Length.Kind == LengthKind.Eof) _diagnostics.Warning("A sized value with length eof is the same as the plain value", source, path);
                CheckType(sized.Inner, source, path, earlier);
                break;
            case OptionalRef optional:
                CheckType(optional.Inner, source, path, earlier);
                break;
            case FixedPointRef fixedPoint when fixedPoint.FractionBits < 0 || fixedPoint.FractionBits >= Primitives.Bits(fixedPoint.Base.Kind):
                _diagnostics.Error($"Invalid number of fraction bits {fixedPoint.FractionBits}", source, path);
                break;
            case NativeRef native:
                BoundNative bound;
                try
                {
                    bound = _project.Bind(native);
                }
                catch (FormatDefinitionException e)
                {
                    _diagnostics.Error(e.Message, source, path);
                    break;
                }

                // Type arguments are read by the native type's code, outside the struct: they can't use its fields.
                for (var i = 0; i < bound.Args.Parameters.Count; i++)
                {
                    var parameter = bound.Args.Parameters[i];
                    if (parameter.Kind == NativeParameterKind.Type) CheckType(bound.Args.Type(parameter.Name), source, $"{path}<{parameter.Name}>", null);
                }

                break;
        }
    }

    private void CheckLength(LengthSpec length, SourceLocation? source, string path, IReadOnlySet<string>? earlier, bool allowZero)
    {
        switch (length.Kind)
        {
            case LengthKind.Zero when !allowZero:
                _diagnostics.Error("Zero-terminated length only applies to strings", source, path);
                break;
            case LengthKind.Expression when earlier == null:
                _diagnostics.Error($"Length expression '{length.Expression}' can only be used in a struct field", source, path);
                break;
            case LengthKind.Expression:
                CheckExpression(length.Expression, earlier!, null, source, path, "length");
                break;
            case LengthKind.Fixed when length.Fixed < 0:
                _diagnostics.Error("A fixed length can't be negative", source, path);
                break;
            case LengthKind.Until:
                _diagnostics.Error("'until' only applies to arrays", source, path);
                break;
        }
    }

    /// <summary>The end condition of an array is evaluated over the fields of each element, which must be a struct.</summary>
    private void CheckUntil(ArrayRef array, SourceLocation? source, string path)
    {
        if (_project.Unalias(array.Element) is not NamedRef element || _project.Find(element.Name) is not StructDef structDef)
        {
            _diagnostics.Error("An array with 'until' needs struct elements, whose fields the condition uses", source, path);
            return;
        }

        CheckExpression(array.Length.Expression, structDef.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal), null, source, path, "until");
    }

    private void CheckVariant(VariantDef variant)
    {
        var kind = _project.TagKind(variant);
        var tagEnum = variant.Tag is NamedRef tagRef ? _project.Find(tagRef.Name) as EnumDef : null;
        var resolvedTag = variant.Tag != null && kind != VariantTagKind.Enum ? _project.Unalias(variant.Tag) : null;
        if (variant.Tag is NamedRef named && tagEnum == null && resolvedTag is not (StringRef or PrimitiveRef))
            _diagnostics.Error($"Variant {variant.Name}: the tag '{named.Name}' must be an enum, an integer or a string", variant.Source);
        else if (resolvedTag is PrimitiveRef primitive && !Primitives.IsInteger(primitive.Kind))
            _diagnostics.Error($"Variant {variant.Name}: the tag must be an integer type", variant.Source);
        else if (resolvedTag is StringRef { Length.Kind: LengthKind.Eof or LengthKind.Expression })
            _diagnostics.Error($"Variant {variant.Name}: a string tag needs a length prefix, a fixed length or a terminator", variant.Source);

        var tags = new Dictionary<object, string>();
        foreach (var variantCase in variant.Cases)
        {
            var path = $"{variant.Name}.{variantCase.Name}";
            object? tag = variantCase.TagValue;
            switch (kind)
            {
                case VariantTagKind.String when variantCase.TagMember == null:
                    _diagnostics.Error($"Case {variantCase.Name} needs a string tag", variant.Source, path);
                    break;
                case VariantTagKind.String:
                    tag = variantCase.TagMember;
                    break;
                case VariantTagKind.Enum when variantCase.TagMember != null:
                    if (tagEnum!.FindByName(variantCase.TagMember) is { } member) tag = member.Value;
                    else _diagnostics.Error($"'{variantCase.TagMember}' is not a member of {tagEnum.Name}", variant.Source, path);
                    break;
                case VariantTagKind.Integer when variantCase.TagMember != null:
                    _diagnostics.Error($"Case tag '{variantCase.TagMember}' needs an enum or a string tag type", variant.Source, path);
                    break;
            }

            if (tag != null && !tags.TryAdd(tag, variantCase.Name))
                _diagnostics.Error($"Cases {tags[tag]} and {variantCase.Name} have the same tag {tag}", variant.Source, path);
            if (variantCase.Type != null) CheckType(variantCase.Type, variant.Source, path, null);
        }
    }

    private void CheckPacketIds()
    {
        var protocols = _project.Settings.Protocols;
        if (protocols.Count == 0)
        {
            foreach (var group in _project.Packets.Where(p => p.Id != null)
                         .GroupBy(p => (p.Id, string.Join(",", p.Groups.Order(StringComparer.Ordinal))))
                         .Where(g => g.Count() > 1))
            {
                _diagnostics.Error($"Packets {string.Join(", ", group.Select(p => p.Name))} share id {group.Key.Id}", group.First().Source);
            }
        }
        else
        {
            CheckProtocols();
        }

        foreach (var packet in _project.Packets.Where(p => p.Id == null))
            _diagnostics.Warning($"Packet {packet.Name} has no id", packet.Source);
    }

    private void CheckProtocols()
    {
        var file = _project.FilePath is { } path ? new SourceLocation(_project.Relative(path)) : null;
        foreach (var duplicate in _project.Settings.Protocols.GroupBy(p => p.Name).Where(g => g.Count() > 1))
            Protocol(Severity.Error, $"Two protocols are named '{duplicate.Key}'", file);

        foreach (var protocol in _project.Settings.Protocols)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(protocol.Name, "^[A-Za-z_][A-Za-z0-9_]*$"))
                Protocol(Severity.Error, $"Protocol name '{protocol.Name}' is not an identifier", file);
            else if (_project.Find(protocol.Name) != null)
                Protocol(Severity.Error, $"Protocol {protocol.Name} has the name of a definition", file);

            var packets = _project.PacketsOf(protocol).Where(p => p.Id != null).ToList();
            if (packets.Count == 0)
                Protocol(Severity.Warning, $"Protocol {protocol.Name} has no packets (none is in all of the groups {string.Join(", ", protocol.Groups)})", file);
            foreach (var duplicate in packets.GroupBy(p => p.Id).Where(g => g.Count() > 1))
            {
                Protocol(Severity.Error, $"Packets {string.Join(", ", duplicate.Select(p => p.Name))} share id {duplicate.Key} in protocol {protocol.Name}",
                    duplicate.First().Source);
            }
        }

        foreach (var packet in _project.Packets.Where(p => p.Id != null && !_project.ProtocolsOf(p).Any()))
            Protocol(Severity.Warning, $"Packet {packet.Name} is in no protocol, so no registry dispatches it", packet.Source);
    }

    /// <summary>A finding that is fixed on the protocols (or in a packet's groups).</summary>
    private void Protocol(Severity severity, string message, SourceLocation? source) =>
        _diagnostics.Add(new Diagnostic(severity, message, source) { Area = Diagnostic.ProtocolsArea });

    private void CheckNativeDefaults()
    {
        var file = _project.FilePath is { } path ? new SourceLocation(_project.Relative(path)) : null;
        foreach (var (name, args) in _project.Defaults.Natives)
        {
            if (_project.Natives.Find(name) == null)
            {
                _diagnostics.Error($"Unknown default '{name}': not a setting ({string.Join(", ", FormatDefaults.Names)}) or a native type", file);
                continue;
            }

            try
            {
                _project.Bind(new NativeRef(name, args));
            }
            catch (FormatDefinitionException e)
            {
                _diagnostics.Error($"defaults.{name}: {e.Message}", file);
            }
        }
    }

    private void CheckLibrarySettings()
    {
        var settings = _project.Settings;
        var file = _project.FilePath is { } path ? new SourceLocation(_project.Relative(path)) : null;
        foreach (var dependency in settings.Dependencies)
        {
            if (!TypeExpressions.IsIdentifier(dependency.AliasOrName))
                _diagnostics.Error($"Library name or alias '{dependency.AliasOrName}' is not an identifier; give it one with 'as'", file);
        }

        foreach (var duplicate in settings.Dependencies.GroupBy(d => d.AliasOrName).Where(g => g.Count() > 1))
            _diagnostics.Error($"Two dependencies are called '{duplicate.Key}'; give one of them another name with 'as'", file);

        if (settings.Kind != ProjectKind.Library)
        {
            if (settings.Defaults.Inherit.Count > 0 || settings.Defaults.InheritAll)
                _diagnostics.Warning("defaults.inherit only applies to libraries (kind: library)", file);
            return;
        }

        foreach (var name in settings.Defaults.Inherit.Where(n => !FormatDefaults.Names.Contains(n) && _project.Natives.Find(n) == null))
            _diagnostics.Warning($"defaults.inherit: '{name}' is neither a setting ({string.Join(", ", FormatDefaults.Names)}) nor a native type", file);

        if (!TypeExpressions.IsIdentifier(settings.Name))
            _diagnostics.Error($"A library's name must be an identifier, as its definitions are referenced as {settings.Name}.Name", file);
        if (settings.Version == null) _diagnostics.Warning("A library needs a version to be packed", file);
        foreach (var packet in _project.Packets)
            _diagnostics.Error($"Libraries can't have packets yet; make {packet.Name} a struct", packet.Source);
        if (settings.Protocols.Count > 0) _diagnostics.Error("Libraries can't have protocols", file);
    }

    private void CheckAliasCycles()
    {
        foreach (var alias in _project.Types.Values.OfType<AliasDef>())
        {
            try
            {
                _project.Unalias(new NamedRef(alias.Name));
            }
            catch (FormatDefinitionException e)
            {
                _diagnostics.Error(e.Message, alias.Source);
            }
        }
    }
}
