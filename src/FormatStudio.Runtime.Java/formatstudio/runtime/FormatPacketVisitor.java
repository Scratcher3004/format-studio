package formatstudio.runtime;

import java.io.IOException;

/** Receives packets from the generated id dispatch. */
public interface FormatPacketVisitor {
    void visit(FormatPacket packet) throws IOException;
}
