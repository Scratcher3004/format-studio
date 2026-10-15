package formatstudio.runtime;

/** A packet: a type with an id, dispatched by the generated registry. */
public interface FormatPacket extends FormatSerializable {
    int packetId();
}
