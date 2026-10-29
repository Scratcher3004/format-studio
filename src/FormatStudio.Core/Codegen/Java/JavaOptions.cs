using System.Text.Json.Nodes;

namespace FormatStudio.Codegen.Java;

/// <summary>How the Java generator represents unsigned integers, which Java doesn't have.</summary>
public enum JavaUnsigned
{
    /// <summary>The next larger signed type (u8/u16 → int, u32 → long); 64-bit values keep their bits in a long.</summary>
    Widen,
    /// <summary>The signed type of the same size (u8 → byte, ...), keeping the bits.</summary>
    Signed,
    /// <summary>Like <see cref="Signed"/>, but values that don't fit (negative ones) are rejected when reading and writing.</summary>
    Checked,
}

/// <summary>What arrays become in Java.</summary>
public enum JavaCollections
{
    List,
    Array,
}

/// <summary>Options of the Java generator.</summary>
public sealed class JavaOptions
{
    public string Package { get; set; } = "generated";
    /// <summary>The package of FormatReader, FormatWriter and the interfaces.</summary>
    public string RuntimePackage { get; set; } = "formatstudio.runtime";
    /// <summary>Write the runtime's sources into the output, in <see cref="RuntimePackage"/>.</summary>
    public bool IncludeRuntime { get; set; } = true;
    /// <summary>Name of the class with the packet id dispatch.</summary>
    public string Registry { get; set; } = "Packets";
    /// <summary>Types that are referenced but not generated (from the definitions' <c>skipCodegen</c>).</summary>
    public IReadOnlySet<string> Exclude { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>Check min/max of numeric fields when reading.</summary>
    public bool ValidateRanges { get; set; }
    /// <summary>Reject invalid UTF-8 and, where <c>check</c> is strict, padding or fill that wouldn't re-encode the same.</summary>
    public bool ValidateStrings { get; set; } = true;
    public JavaUnsigned Unsigned { get; set; } = JavaUnsigned.Widen;
    public JavaCollections Collections { get; set; } = JavaCollections.List;
    /// <summary>Structs and packets become records (Java 16+) instead of classes with public fields.</summary>
    public bool Records { get; set; }

    public static IReadOnlyList<CodegenOption> Describe() =>
    [
        CodegenOption.Text("package", "Package", "generated", "The package of the generated classes."),
        CodegenOption.Text("registry", "Registry class", "Packets", "The class with the packet id dispatch."),
        CodegenOption.Choice("unsigned", "Unsigned integers", ["widen", "signed", "checked"],
            "widen: the next larger type (u8/u16 → int, u32 → long; u64 keeps its bits in a long, with a warning). signed: the signed type of the same size, keeping the bits. checked: like signed, but values that don't fit are rejected."),
        CodegenOption.Choice("collections", "Arrays as", ["list", "array"], "java.util.List or Java arrays (T[]). Maps are always lists of entries."),
        CodegenOption.Bool("records", "Records", false, "Structs and packets become records (needs Java 16) instead of classes with public fields."),
        CodegenOption.Bool("validateRanges", "Validate ranges", false, "Check min/max of numeric fields when reading."),
        CodegenOption.Bool("validateStrings", "Validate strings", true, "Reject invalid UTF-8, and padding or fill that wouldn't re-encode the same where check is strict."),
        CodegenOption.Bool("includeRuntime", "Include runtime", true,
            "Write the runtime sources (FormatReader, FormatWriter, interfaces) into the output. Turn off when the runtime is a dependency."),
        CodegenOption.Text("runtimePackage", "Runtime package", "formatstudio.runtime", "The package of the runtime the generated code calls.", true),
    ];

    public static JavaOptions From(JsonObject options)
    {
        var read = new OptionReader(options, Describe());
        return new JavaOptions
        {
            Package = read.Text("package"),
            Registry = read.Text("registry"),
            ValidateRanges = read.Bool("validateRanges"),
            ValidateStrings = read.Bool("validateStrings"),
            Unsigned = Enum.Parse<JavaUnsigned>(read.Choice("unsigned"), true),
            Collections = Enum.Parse<JavaCollections>(read.Choice("collections"), true),
            Records = read.Bool("records"),
            IncludeRuntime = read.Bool("includeRuntime"),
            RuntimePackage = read.Text("runtimePackage"),
        };
    }
}
