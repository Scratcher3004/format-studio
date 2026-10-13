using FormatStudio.Expressions;
using FormatStudio.Model;
using FormatStudio.Projects;

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
                var target = _project.Find(named.Name);
                if (target == null)
                {
                    _diagnostics.Error($"Unknown type '{named.Name}'", source, path);
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
            case NbtRef { Flavor: null } when _project.Defaults.Nbt == null:
                _diagnostics.Error("NBT without a flavor: set defaults.nbt in the project or write {nbt: <flavor>}", source, path);
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
        }
    }

    private void CheckVariant(VariantDef variant)
    {
        EnumDef? tagEnum = null;
        if (variant.Tag is NamedRef tagRef)
        {
            tagEnum = _project.Find(tagRef.Name) as EnumDef;
            if (tagEnum == null) _diagnostics.Error($"Variant {variant.Name}: the tag '{tagRef.Name}' must be an enum", variant.Source);
        }
        else if (variant.Tag is PrimitiveRef primitive && !Primitives.IsInteger(primitive.Kind))
        {
            _diagnostics.Error($"Variant {variant.Name}: the tag must be an integer type", variant.Source);
        }

        var tags = new Dictionary<long, string>();
        foreach (var variantCase in variant.Cases)
        {
            var path = $"{variant.Name}.{variantCase.Name}";
            long? tag = variantCase.TagValue;
            if (variantCase.TagMember != null)
            {
                if (tagEnum == null) _diagnostics.Error($"Case tag '{variantCase.TagMember}' needs an enum tag type", variant.Source, path);
                else if (tagEnum.FindByName(variantCase.TagMember) is { } member) tag = member.Value;
                else _diagnostics.Error($"'{variantCase.TagMember}' is not a member of {tagEnum.Name}", variant.Source, path);
            }

            if (tag is { } value && !tags.TryAdd(value, variantCase.Name))
                _diagnostics.Error($"Cases {tags[value]} and {variantCase.Name} have the same tag {value}", variant.Source, path);
            if (variantCase.Type != null) CheckType(variantCase.Type, variant.Source, path, null);
        }
    }

    private void CheckPacketIds()
    {
        foreach (var group in _project.Packets.Where(p => p.Id != null)
                     .GroupBy(p => (p.Id, string.Join(",", p.Groups.Order(StringComparer.Ordinal))))
                     .Where(g => g.Count() > 1))
        {
            _diagnostics.Error($"Packets {string.Join(", ", group.Select(p => p.Name))} share id {group.Key.Id}", group.First().Source);
        }

        foreach (var packet in _project.Packets.Where(p => p.Id == null))
            _diagnostics.Warning($"Packet {packet.Name} has no id", packet.Source);
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
