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

/// <summary>A packet with a text id (e.g. a channel name such as <c>mod:move</c>), dispatched by the generated registry.</summary>
public interface IFormatStreamTextPacket<TSelf> : IFormatStreamSerializable<TSelf> where TSelf : IFormatStreamTextPacket<TSelf>
{
    static abstract string PacketId { get; }
}

/// <summary>Receives packets with text ids from the generated dispatch without boxing.</summary>
public interface IFormatStreamTextPacketVisitor
{
    void Visit<T>(ref T packet) where T : IFormatStreamTextPacket<T>;
}
