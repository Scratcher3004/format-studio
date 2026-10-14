using FormatStudio.Model;

namespace FormatStudio.Web;

/// <summary>The dec/hex/bin toggle on integer inputs: an adornment that shows the base and switches to the next one.</summary>
public static class NumberBases
{
    public static NumberBase Next(NumberBase numberBase) => numberBase switch
    {
        NumberBase.Decimal => NumberBase.Hex,
        NumberBase.Hex => NumberBase.Binary,
        _ => NumberBase.Decimal,
    };

    public static string Label(NumberBase numberBase) => numberBase switch
    {
        NumberBase.Hex => "hex",
        NumberBase.Binary => "bin",
        _ => "dec",
    };

    /// <summary>The label as an SVG icon, so it can be a clickable adornment.</summary>
    public static string Icon(NumberBase numberBase) =>
        $"<text x=\"12\" y=\"16\" text-anchor=\"middle\" font-size=\"11\" font-weight=\"600\" font-family=\"ui-monospace, Consolas, monospace\" fill=\"currentColor\">{Label(numberBase)}</text>";

    public static string Hint(NumberBase numberBase) => $"Shown as {Label(numberBase)}; click for {Label(Next(numberBase))}";
}
