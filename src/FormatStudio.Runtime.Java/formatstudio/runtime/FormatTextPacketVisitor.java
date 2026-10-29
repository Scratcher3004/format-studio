package formatstudio.runtime;

import java.io.IOException;

/** Receives packets with text ids from the generated dispatch. */
public interface FormatTextPacketVisitor {
    void visit(FormatTextPacket packet) throws IOException;
}
