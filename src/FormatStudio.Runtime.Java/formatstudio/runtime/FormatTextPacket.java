package formatstudio.runtime;

/** A packet with a text id (e.g. a channel name such as "mod:move"), dispatched by the generated registry. */
public interface FormatTextPacket extends FormatSerializable {
    String packetId();
}
