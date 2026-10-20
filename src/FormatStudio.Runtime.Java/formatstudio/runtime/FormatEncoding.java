package formatstudio.runtime;

import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;

/** The text encodings generated code reads and writes strings in. */
public enum FormatEncoding {
    UTF8(StandardCharsets.UTF_8, "UTF-8"),
    /** 7-bit ASCII: other bytes and characters are errors (or U+FFFD when reading leniently). */
    ASCII(StandardCharsets.US_ASCII, "ASCII"),
    /** ISO 8859-1: every byte is a character; characters above U+00FF can't be written. */
    LATIN1(StandardCharsets.ISO_8859_1, "Latin-1");

    final Charset charset;
    final String label;

    FormatEncoding(Charset charset, String label) {
        this.charset = charset;
        this.label = label;
    }
}
