using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Codegen.CSharp;

/// <summary>
/// The names generated C# gives definitions and variant factories. A C# member can't have the name of its type, so a
/// definition named like a member the generator gives it (<c>Read</c>, <c>Write</c>; <c>Id</c> and <c>PacketId</c> of
/// packets; <c>Kind</c>, <c>Case</c>, <c>WriteContent</c>, <c>CaseOf</c>, <c>TagOf</c> and the <c>From</c> factories of
/// variants) gets <c>Type</c> appended.
/// </summary>
public static class CSharpNames
{
    /// <param name="find">Resolves a type name as written in the definition (a variant's tag), by default <see cref="FormatProject.Find"/>.</param>
    public static string TypeName(FormatProject project, TypeDef definition, Func<string, TypeDef?>? find = null)
    {
        var members = GeneratedMembers(project, definition, find ?? project.Find);
        var name = definition.Name;
        while (members.Contains(name)) name += "Type";
        return name;
    }

    /// <summary>The factory of a variant case: <c>From</c> and its name, with <c>Value</c> when a case's field has that name.</summary>
    public static string Factory(VariantDef variant, VariantCase variantCase)
    {
        var name = "From" + variantCase.Name;
        return variant.Cases.Any(c => c.Type != null && c.Name == name) ? name + "Value" : name;
    }

    private static HashSet<string> GeneratedMembers(FormatProject project, TypeDef definition, Func<string, TypeDef?> find)
    {
        switch (definition)
        {
            case StructDef structDef:
                return structDef.IsPacket ? ["Read", "Write", "Id", "PacketId"] : ["Read", "Write"];
            case BitfieldDef:
                return ["Read", "Write"];
            case VariantDef variant:
                var members = new HashSet<string>(StringComparer.Ordinal) { "Read", "Write", "WriteContent", "Kind" };
                // Without an enum tag, the cases are a nested enum Case.
                if (!(variant.Tag is NamedRef named && find(named.Name) is EnumDef)) members.Add("Case");
                if (project.TagKind(variant) == VariantTagKind.String) members.UnionWith(["CaseOf", "TagOf"]);
                foreach (var variantCase in variant.Cases) members.Add(Factory(variant, variantCase));
                return members;
            default:
                return [];
        }
    }
}
