using System.Text.Json.Nodes;

namespace FormatStudio.Model;

public sealed record SourceLocation(string File, int Line = 0, int Column = 0)
{
    public override string ToString() => Line > 0 ? $"{File}:{Line}:{Column}" : File;
}

/// <summary>A named definition: one DSL file.</summary>
public abstract class TypeDef
{
    public required string Name { get; set; }
    /// <summary>Human-readable name, e.g. the original name from an imported schema.</summary>
    public string? Title { get; set; }
    public string? Doc { get; set; }
    public SourceLocation? Source { get; set; }

    /// <summary>The DSL keyword: struct, packet, enum, variant, alias, bitfield.</summary>
    public abstract string Kind { get; }
}

public sealed class FieldDef
{
    public required string Name { get; set; }
    public string? Title { get; set; }
    public string? Doc { get; set; }
    public required TypeRef Type { get; set; }

    /// <summary>The field is only present when this expression is true (no presence byte).</summary>
    public string? If { get; set; }

    /// <summary>The field always has this value. It is written as is and checked when read.</summary>
    public JsonNode? Const { get; set; }

    /// <summary>The value is calculated from this expression when encoding (e.g. <c>len(items)</c>).</summary>
    public string? Computed { get; set; }

    public JsonNode? Default { get; set; }
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public int? MinItems { get; set; }
    public int? MaxItems { get; set; }
    public string? Pattern { get; set; }

    public SourceLocation? Source { get; set; }

    /// <summary>The field is part of the value the user supplies (not const and not computed).</summary>
    public bool IsInput => Const == null && Computed == null;
}

/// <summary>A sequence of fields. Packets are structs with an id.</summary>
public sealed class StructDef : TypeDef
{
    public List<FieldDef> Fields { get; } = [];
    public bool IsPacket { get; set; }
    public long? Id { get; set; }
    /// <summary>Free-form packet groups, e.g. a direction or a protocol state.</summary>
    public List<string> Groups { get; } = [];

    public override string Kind => IsPacket ? "packet" : "struct";
}

public sealed class EnumMember
{
    public required string Name { get; set; }
    public long Value { get; set; }
    public string? Title { get; set; }
    public string? Doc { get; set; }
}

public sealed class EnumDef : TypeDef
{
    /// <summary>The default wire type. Use sites may override it with <c>as</c>.</summary>
    public PrimitiveRef Type { get; set; } = new(PrimitiveKind.VarUInt32);
    public List<EnumMember> Members { get; } = [];
    public bool Flags { get; set; }
    /// <summary>Values that are not members are kept instead of reported as errors.</summary>
    public bool AllowUnknown { get; set; }

    public override string Kind => "enum";

    public EnumMember? FindByName(string name) => Members.FirstOrDefault(m => m.Name == name);
    public EnumMember? FindByValue(long value) => Members.FirstOrDefault(m => m.Value == value);
}

public sealed class VariantCase
{
    public required string Name { get; set; }
    /// <summary>Numeric tag, or null when <see cref="TagMember"/> gives the tag.</summary>
    public long? TagValue { get; set; }
    /// <summary>The enum member name of the tag, or the tag itself when the tags are strings.</summary>
    public string? TagMember { get; set; }
    /// <summary>The payload, or null for a case without data.</summary>
    public TypeRef? Type { get; set; }
    public string? Title { get; set; }
    public string? Doc { get; set; }
}

public enum VariantTagKind
{
    /// <summary>Numbers.</summary>
    Integer,
    /// <summary>Members of the enum named by <see cref="VariantDef.Tag"/>.</summary>
    Enum,
    /// <summary>Strings, e.g. registry names.</summary>
    String,
}

/// <summary>
/// A tagged union. The tag is read before the case when <see cref="Tag"/> is set, otherwise the use site supplies it
/// as an expression (<c>{type: X, tag: "kind"}</c>).
/// </summary>
public sealed class VariantDef : TypeDef
{
    /// <summary>A primitive, a string type or a reference to an enum. Null when the tag is always external.</summary>
    public TypeRef? Tag { get; set; }
    public List<VariantCase> Cases { get; } = [];

    public override string Kind => "variant";
}

/// <summary>Another name for a type expression.</summary>
public sealed class AliasDef : TypeDef
{
    public required TypeRef Target { get; set; }
    public override string Kind => "alias";
}

public sealed class BitFieldMember
{
    public required string Name { get; set; }
    public int Bits { get; set; }
    public bool Signed { get; set; }
    /// <summary>Optional interpretation: <c>bool</c> or an enum name.</summary>
    public string? Type { get; set; }
    public string? Doc { get; set; }
}

/// <summary>An integer split into bit ranges, first member in the most significant bits.</summary>
public sealed class BitfieldDef : TypeDef
{
    public PrimitiveRef Base { get; set; } = new(PrimitiveKind.U8);
    public List<BitFieldMember> Members { get; } = [];
    /// <summary>When true the first member occupies the least significant bits.</summary>
    public bool LsbFirst { get; set; }

    public override string Kind => "bitfield";
}
