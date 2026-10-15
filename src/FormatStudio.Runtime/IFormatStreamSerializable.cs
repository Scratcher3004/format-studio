namespace FormatStudio.Runtime;

/// <summary>A type with generated <c>Read</c>/<c>Write</c> methods over streams (generator <c>csharp-stream</c>).</summary>
public interface IFormatStreamSerializable<TSelf> where TSelf : IFormatStreamSerializable<TSelf>
{
    static abstract TSelf Read(FormatStreamReader reader);

    void Write(FormatStreamWriter writer);
}

/// <summary>A packet: a type with an id, dispatched by the generated registry.</summary>
public interface IFormatStreamPacket<TSelf> : IFormatStreamSerializable<TSelf> where TSelf : IFormatStreamPacket<TSelf>
{
    static abstract uint PacketId { get; }
}

/// <summary>Receives packets from the generated id dispatch without boxing.</summary>
public interface IFormatStreamPacketVisitor
{
    void Visit<T>(ref T packet) where T : IFormatStreamPacket<T>;
}
