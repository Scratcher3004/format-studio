using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Codegen.Java;

/// <summary>
/// The names generated Java gives definitions. A variant without a closed enum tag has a nested enum <c>Case</c>, which
/// can't share the name of its class: such a variant named <c>Case</c> becomes <c>CaseType</c>.
/// </summary>
public static class JavaNames
{
    /// <param name="find">Resolves a type name as written in the definition (a variant's tag), by default <see cref="FormatProject.Find"/>.</param>
    public static string TypeName(FormatProject project, TypeDef definition, Func<string, TypeDef?>? find = null) =>
        definition is VariantDef variant && definition.Name == "Case" && HasCaseEnum(variant, find ?? project.Find) ? "CaseType" : definition.Name;

    /// <summary>Whether a variant's cases are a nested enum <c>Case</c>: its tag isn't an enum, or one open to unknown values.</summary>
    internal static bool HasCaseEnum(VariantDef variant, Func<string, TypeDef?> find) =>
        !(variant.Tag is NamedRef named && find(named.Name) is EnumDef { Flags: false, AllowUnknown: false });
}
