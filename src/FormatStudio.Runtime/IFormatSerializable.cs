namespace FormatStudio.Runtime;

/// <summary>A type with generated <c>Read</c>/<c>Write</c> methods.</summary>
public interface IFormatSerializable<TSelf> where TSelf : IFormatSerializable<TSelf>
{
    static abstract TSelf Read(ref FormatReader reader);

    void Write(ref FormatWriter writer);
}

/// <summary>A packet: a type with an id, dispatched by the generated registry.</summary>
public interface IFormatPacket<TSelf> : IFormatSerializable<TSelf> where TSelf : IFormatPacket<TSelf>
{
    static abstract uint PacketId { get; }
}

/// <summary>Receives packets from the generated id dispatch without boxing.</summary>
public interface IFormatPacketVisitor
{
    void Visit<T>(ref T packet) where T : IFormatPacket<T>;
}

/// <summary>Malformed or unsupported data.</summary>
public sealed class FormatDataException(string message) : Exception(message);
