package formatstudio.runtime;

import java.io.IOException;

/** Malformed or unsupported data. */
public final class FormatDataException extends IOException {
    private static final long serialVersionUID = 1L;

    public FormatDataException(String message) {
        super(message);
    }
}
