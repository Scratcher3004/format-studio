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

/// <summary>A packet with a text id (e.g. a channel name such as <c>mod:move</c>), dispatched by the generated registry.</summary>
public interface IFormatTextPacket<TSelf> : IFormatSerializable<TSelf> where TSelf : IFormatTextPacket<TSelf>
{
    static abstract string PacketId { get; }
}

/// <summary>Receives packets with text ids from the generated dispatch without boxing.</summary>
public interface IFormatTextPacketVisitor
{
    void Visit<T>(ref T packet) where T : IFormatTextPacket<T>;
}

/// <summary>Malformed or unsupported data.</summary>
public sealed class FormatDataException(string message) : Exception(message);
