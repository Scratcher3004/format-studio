namespace FormatStudio.Web;

/// <summary>How the definition builder offers turning a struct into a packet.</summary>
public enum PacketSwitchMode
{
    Shown,
    /// <summary>Shown but off (in libraries); a packet can still be turned into a struct.</summary>
    Disabled,
    /// <summary>Not shown (new definitions, whose kind comes from their template).</summary>
    Hidden,
}
