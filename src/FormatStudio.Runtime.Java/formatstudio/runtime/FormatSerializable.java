package formatstudio.runtime;

import java.io.IOException;

/** A type with a generated {@code write} method (and a static {@code read(FormatReader)}). */
public interface FormatSerializable {
    void write(FormatWriter writer) throws IOException;
}
