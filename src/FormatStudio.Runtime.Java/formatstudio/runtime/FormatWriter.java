package formatstudio.runtime;

import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.UUID;

/**
 * Writes wire primitives to an {@link OutputStream}. Counterpart of {@link FormatReader}; takes the same Java types
 * the reader returns. Wrap unbuffered streams (sockets, files) in a {@link java.io.BufferedOutputStream}.
 */
public final class FormatWriter {
    // Empty compound tags: type, empty root name (except Java network), end.
    private static final byte[] EMPTY_NBT_JAVA = {0x0A, 0x00, 0x00, 0x00};
    private static final byte[] EMPTY_NBT_JAVA_NETWORK = {0x0A, 0x00};
    private static final byte[] EMPTY_NBT_BEDROCK = {0x0A, 0x00, 0x00, 0x00};
    private static final byte[] EMPTY_NBT_BEDROCK_NETWORK = {0x0A, 0x00, 0x00};

    private final OutputStream output;
    private final byte[] scratch = new byte[10];
    private long written;

    public FormatWriter(OutputStream output) {
        this.output = output;
    }

    /** Number of bytes written by this writer. */
    public long written() {
        return written;
    }

    /** The number of bytes {@link #writeUtf8} writes for a string. */
    public static int utf8Length(String value) {
        return value == null ? 0 : value.getBytes(StandardCharsets.UTF_8).length;
    }

    private void put(int count) throws IOException {
        output.write(scratch, 0, count);
        written += count;
    }

    public void writeBytes(byte[] bytes) throws IOException {
        writeBytes(bytes, 0, bytes.length);
    }

    public void writeBytes(byte[] bytes, int offset, int count) throws IOException {
        output.write(bytes, offset, count);
        written += count;
    }

    public void writeBool(boolean value) throws IOException {
        writeU8(value ? 1 : 0);
    }

    public void writeU8(int value) throws IOException {
        output.write(value);
        written++;
    }

    public void writeI8(byte value) throws IOException {
        writeU8(value);
    }

    public void writeU16LE(int value) throws IOException {
        scratch[0] = (byte) value;
        scratch[1] = (byte) (value >>> 8);
        put(2);
    }

    public void writeU16BE(int value) throws IOException {
        scratch[0] = (byte) (value >>> 8);
        scratch[1] = (byte) value;
        put(2);
    }

    public void writeI16LE(short value) throws IOException {
        writeU16LE(value);
    }

    public void writeI16BE(short value) throws IOException {
        writeU16BE(value);
    }

    public void writeI32LE(int value) throws IOException {
        for (int i = 0; i < 4; i++) scratch[i] = (byte) (value >>> (8 * i));
        put(4);
    }

    public void writeI32BE(int value) throws IOException {
        for (int i = 0; i < 4; i++) scratch[i] = (byte) (value >>> (24 - 8 * i));
        put(4);
    }

    public void writeU32LE(long value) throws IOException {
        writeI32LE((int) value);
    }

    public void writeU32BE(long value) throws IOException {
        writeI32BE((int) value);
    }

    public void writeI64LE(long value) throws IOException {
        for (int i = 0; i < 8; i++) scratch[i] = (byte) (value >>> (8 * i));
        put(8);
    }

    public void writeI64BE(long value) throws IOException {
        for (int i = 0; i < 8; i++) scratch[i] = (byte) (value >>> (56 - 8 * i));
        put(8);
    }

    /** Writes the bits of an unsigned 64-bit value. */
    public void writeU64LE(long value) throws IOException {
        writeI64LE(value);
    }

    public void writeU64BE(long value) throws IOException {
        writeI64BE(value);
    }

    public void writeF32LE(float value) throws IOException {
        writeI32LE(Float.floatToRawIntBits(value));
    }

    public void writeF32BE(float value) throws IOException {
        writeI32BE(Float.floatToRawIntBits(value));
    }

    public void writeF64LE(double value) throws IOException {
        writeI64LE(Double.doubleToRawLongBits(value));
    }

    public void writeF64BE(double value) throws IOException {
        writeI64BE(Double.doubleToRawLongBits(value));
    }

    /** Writes the low 32 bits as an unsigned varint. */
    public void writeVarUInt32(long value) throws IOException {
        writeVarUInt64(value & 0xFFFFFFFFL);
    }

    /** Writes the bits of an unsigned 64-bit value as a varint. */
    public void writeVarUInt64(long value) throws IOException {
        int count = 0;
        while ((value & ~0x7FL) != 0) {
            scratch[count++] = (byte) (value | 0x80);
            value >>>= 7;
        }

        scratch[count++] = (byte) value;
        put(count);
    }

    public void writeVarInt32(int value) throws IOException {
        writeVarUInt64(value & 0xFFFFFFFFL);
    }

    public void writeVarInt64(long value) throws IOException {
        writeVarUInt64(value);
    }

    public void writeZigZag32(int value) throws IOException {
        writeVarUInt64(((value << 1) ^ (value >> 31)) & 0xFFFFFFFFL);
    }

    public void writeZigZag64(long value) throws IOException {
        writeVarUInt64((value << 1) ^ (value >> 63));
    }

    public void writeUuidLE(UUID value) throws IOException {
        writeI64LE(value.getMostSignificantBits());
        writeI64LE(value.getLeastSignificantBits());
    }

    public void writeUuidBE(UUID value) throws IOException {
        writeI64BE(value.getMostSignificantBits());
        writeI64BE(value.getLeastSignificantBits());
    }

    /** Writes the UTF-8 bytes only; the caller writes the length. */
    public void writeUtf8(String value) throws IOException {
        writeBytes(value.getBytes(StandardCharsets.UTF_8));
    }

    public void writeUtf8Zero(String value) throws IOException {
        writeUtf8(value);
        writeU8(0);
    }

    // Raw NBT as read by FormatReader.readNbt*; null writes an empty compound.
    public void writeNbtJava(byte[] raw) throws IOException {
        writeBytes(raw != null ? raw : EMPTY_NBT_JAVA);
    }

    public void writeNbtJavaNetwork(byte[] raw) throws IOException {
        writeBytes(raw != null ? raw : EMPTY_NBT_JAVA_NETWORK);
    }

    public void writeNbtBedrock(byte[] raw) throws IOException {
        writeBytes(raw != null ? raw : EMPTY_NBT_BEDROCK);
    }

    public void writeNbtBedrockNetwork(byte[] raw) throws IOException {
        writeBytes(raw != null ? raw : EMPTY_NBT_BEDROCK_NETWORK);
    }
}
