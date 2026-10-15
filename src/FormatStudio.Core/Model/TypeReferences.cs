using FormatStudio.Expressions;

namespace FormatStudio.Model;

/// <summary>The type names a definition uses: in type expressions, as bit field member types and in enum constants of expressions.</summary>
public static class TypeReferences
{
    public static IEnumerable<string> Of(TypeDef definition) => definition switch
    {
        StructDef structDef => structDef.Fields.SelectMany(f => Of(f.Type).Concat(InExpression(f.If)).Concat(InExpression(f.Computed))),
        VariantDef variant => (variant.Tag != null ? Of(variant.Tag) : []).Concat(variant.Cases.Where(c => c.Type != null).SelectMany(c => Of(c.Type!))),
        AliasDef alias => Of(alias.Target),
        BitfieldDef bitfield => bitfield.Members.Select(m => m.Type).OfType<string>().Where(t => t != "bool"),
        _ => [],
    };

    public static IEnumerable<string> Of(TypeRef type) => type switch
    {
        NamedRef named => InExpression(named.Tag).Prepend(named.Name),
        StringRef str => Of(str.Length),
        BytesRef bytes => Of(bytes.Length),
        ArrayRef array => Of(array.Element).Concat(Of(array.Length)),
        MapRef map => Of(map.Key).Concat(Of(map.Value)).Concat(Of(map.Length)),
        OptionalRef optional => Of(optional.Inner),
        SizedRef sized => Of(sized.Inner).Concat(Of(sized.Length)),
        _ => [],
    };

    private static IEnumerable<string> Of(LengthSpec length) => InExpression(length.Expression);

    private static IEnumerable<string> InExpression(string? text) =>
        text != null && Expr.TryParse(text, out var expression, out _) ? expression!.EnumConstants().Select(c => c.Enum) : [];
}
