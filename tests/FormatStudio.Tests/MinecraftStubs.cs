using System.Net.Http;
using System.Text;
using FormatStudio.Codegen;

namespace FormatStudio.Tests;

/// <summary>
/// What the netty generator's output needs to compile and run without Minecraft: stand-ins for the few Minecraft and
/// DataFixerUpper classes it uses (with the same wire formats), and the netty-buffer jars, which are real.
/// </summary>
internal static class MinecraftStubs
{
    private const string NettyVersion = "4.2.7.Final";

    /// <summary>
    /// The netty-buffer and netty-common jars: from the folder FORMAT_STUDIO_NETTY, else downloaded from Maven Central
    /// once into the temp folder; null when neither works.
    /// </summary>
    public static IReadOnlyList<string>? NettyJars()
    {
        string[] names = ["netty-buffer", "netty-common"];
        if (Environment.GetEnvironmentVariable("FORMAT_STUDIO_NETTY") is { Length: > 0 } folder)
        {
            var jars = names.Select(n => Directory.GetFiles(folder, $"{n}-*.jar").FirstOrDefault()).OfType<string>().ToList();
            return jars.Count == names.Length ? jars : null;
        }

        var cache = Path.Combine(Path.GetTempPath(), "format-studio-netty", NettyVersion);
        Directory.CreateDirectory(cache);
        var result = new List<string>();
        foreach (var name in names)
        {
            var path = Path.Combine(cache, $"{name}-{NettyVersion}.jar");
            if (!File.Exists(path))
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    var bytes = client.GetByteArrayAsync($"https://repo1.maven.org/maven2/io/netty/{name}/{NettyVersion}/{name}-{NettyVersion}.jar").Result;
                    File.WriteAllBytes(path + ".part", bytes);
                    File.Move(path + ".part", path, true);
                }
                catch (Exception e) when (e is HttpRequestException or AggregateException or IOException or TaskCanceledException)
                {
                    return null;
                }
            }

            result.Add(path);
        }

        return result;
    }

    public static IReadOnlyList<GeneratedFile> Files() =>
    [
        .. Enumerable.Range(3, 10).Select(Function),
        new("net/minecraft/network/codec/StreamEncoder.java", """
            package net.minecraft.network.codec;

            @FunctionalInterface
            public interface StreamEncoder<O, T> {
                void encode(O buf, T value);
            }
            """),
        new("net/minecraft/network/codec/StreamDecoder.java", """
            package net.minecraft.network.codec;

            @FunctionalInterface
            public interface StreamDecoder<I, T> {
                T decode(I buf);
            }
            """),
        new("net/minecraft/network/codec/StreamCodec.java", StreamCodec()),
        new("net/minecraft/network/codec/ByteBufCodecs.java", ByteBufCodecs),
        new("net/minecraft/network/RegistryFriendlyByteBuf.java", """
            package net.minecraft.network;

            import io.netty.buffer.ByteBuf;
            import io.netty.buffer.WrappedByteBuf;

            public class RegistryFriendlyByteBuf extends WrappedByteBuf {
                public RegistryFriendlyByteBuf(ByteBuf buf) {
                    super(buf);
                }
            }
            """),
        new("net/minecraft/resources/Identifier.java", """
            package net.minecraft.resources;

            import io.netty.buffer.ByteBuf;
            import net.minecraft.network.codec.ByteBufCodecs;
            import net.minecraft.network.codec.StreamCodec;

            public record Identifier(String text) {
                public static final StreamCodec<ByteBuf, Identifier> STREAM_CODEC = ByteBufCodecs.STRING_UTF8.map(Identifier::new, Identifier::text);

                public static Identifier parse(String text) {
                    return new Identifier(text);
                }
            }
            """),
        new("net/minecraft/network/protocol/common/custom/CustomPacketPayload.java", """
            package net.minecraft.network.protocol.common.custom;

            import net.minecraft.resources.Identifier;

            public interface CustomPacketPayload {
                Type<? extends CustomPacketPayload> type();

                record Type<T extends CustomPacketPayload>(Identifier id) {
                }
            }
            """),
        new("net/minecraft/core/UUIDUtil.java", """
            package net.minecraft.core;

            import io.netty.buffer.ByteBuf;
            import java.util.UUID;
            import net.minecraft.network.codec.StreamCodec;

            public final class UUIDUtil {
                private UUIDUtil() {
                }

                public static final StreamCodec<ByteBuf, UUID> STREAM_CODEC = StreamCodec.of(
                    (buf, uuid) -> buf.writeLong(uuid.getMostSignificantBits()).writeLong(uuid.getLeastSignificantBits()),
                    buf -> new UUID(buf.readLong(), buf.readLong()));
            }
            """),
        new("net/minecraft/nbt/Tag.java", """
            package net.minecraft.nbt;

            /** A tag as its network NBT bytes. */
            public final class Tag {
                public final byte[] bytes;

                public Tag(byte[] bytes) {
                    this.bytes = bytes;
                }
            }
            """),
    ];

    private static GeneratedFile Function(int arity)
    {
        var types = string.Join(", ", Enumerable.Range(1, arity).Select(i => $"T{i}"));
        var parameters = string.Join(", ", Enumerable.Range(1, arity).Select(i => $"T{i} t{i}"));
        return new GeneratedFile($"com/mojang/datafixers/util/Function{arity}.java", $$"""
            package com.mojang.datafixers.util;

            @FunctionalInterface
            public interface Function{{arity}}<{{types}}, R> {
                R apply({{parameters}});
            }
            """);
    }

    private static string StreamCodec()
    {
        var builder = new StringBuilder("""
            package net.minecraft.network.codec;

            import java.util.function.BiFunction;
            import java.util.function.Function;

            public interface StreamCodec<B, V> extends StreamEncoder<B, V>, StreamDecoder<B, V> {
                static <B, V> StreamCodec<B, V> of(StreamEncoder<B, V> encoder, StreamDecoder<B, V> decoder) {
                    return new StreamCodec<B, V>() {
                        @Override
                        public V decode(B buf) {
                            return decoder.decode(buf);
                        }

                        @Override
                        public void encode(B buf, V value) {
                            encoder.encode(buf, value);
                        }
                    };
                }

                static <B, V> StreamCodec<B, V> unit(V value) {
                    return of((buf, ignored) -> { }, buf -> value);
                }

                default <O> StreamCodec<B, O> apply(CodecOperation<B, V, O> operation) {
                    return operation.apply(this);
                }

                default <O> StreamCodec<B, O> map(Function<? super V, ? extends O> to, Function<? super O, ? extends V> from) {
                    return of((buf, value) -> encode(buf, from.apply(value)), buf -> to.apply(decode(buf)));
                }

                @FunctionalInterface
                interface CodecOperation<B, S, T> {
                    StreamCodec<B, T> apply(StreamCodec<B, S> codec);
                }

            """);
        for (var arity = 1; arity <= 12; arity++)
        {
            var range = Enumerable.Range(1, arity).ToList();
            var function = arity switch
            {
                1 => "Function<T1, C>",
                2 => "BiFunction<T1, T2, C>",
                _ => $"com.mojang.datafixers.util.Function{arity}<{string.Join(", ", range.Select(i => $"T{i}"))}, C>",
            };
            builder.Append($"    static <B, C, {string.Join(", ", range.Select(i => $"T{i}"))}> StreamCodec<B, C> composite(");
            builder.Append(string.Join(", ", range.Select(i => $"StreamCodec<? super B, T{i}> c{i}, Function<C, T{i}> g{i}")));
            builder.Append($", {function} f) {{\n");
            builder.Append("        return of((buf, value) -> {\n");
            foreach (var i in range) builder.Append($"            c{i}.encode(buf, g{i}.apply(value));\n");
            builder.Append("        }, buf -> {\n");
            foreach (var i in range) builder.Append($"            T{i} v{i} = c{i}.decode(buf);\n");
            builder.Append($"            return f.apply({string.Join(", ", range.Select(i => $"v{i}"))});\n");
            builder.Append("        });\n    }\n\n");
        }

        return builder.Append("}\n").ToString();
    }

    private const string ByteBufCodecs = """
        package net.minecraft.network.codec;

        import io.netty.buffer.ByteBuf;
        import java.nio.charset.StandardCharsets;
        import java.util.ArrayList;
        import java.util.List;
        import java.util.Optional;
        import net.minecraft.nbt.Tag;

        public interface ByteBufCodecs {
            StreamCodec<ByteBuf, Boolean> BOOL = StreamCodec.of((buf, value) -> buf.writeBoolean(value), ByteBuf::readBoolean);
            StreamCodec<ByteBuf, Byte> BYTE = StreamCodec.of((buf, value) -> buf.writeByte(value), ByteBuf::readByte);
            StreamCodec<ByteBuf, Short> SHORT = StreamCodec.of((buf, value) -> buf.writeShort(value), ByteBuf::readShort);
            StreamCodec<ByteBuf, Integer> UNSIGNED_SHORT = StreamCodec.of((buf, value) -> buf.writeShort(value), ByteBuf::readUnsignedShort);
            StreamCodec<ByteBuf, Integer> INT = StreamCodec.of((buf, value) -> buf.writeInt(value), ByteBuf::readInt);
            StreamCodec<ByteBuf, Integer> VAR_INT = StreamCodec.of((buf, value) -> writeVarLong(buf, value & 0xFFFFFFFFL), buf -> (int) readVarLong(buf, 5));
            StreamCodec<ByteBuf, Long> LONG = StreamCodec.of((buf, value) -> buf.writeLong(value), ByteBuf::readLong);
            StreamCodec<ByteBuf, Long> VAR_LONG = StreamCodec.of((buf, value) -> writeVarLong(buf, value), buf -> readVarLong(buf, 10));
            StreamCodec<ByteBuf, Float> FLOAT = StreamCodec.of((buf, value) -> buf.writeFloat(value), ByteBuf::readFloat);
            StreamCodec<ByteBuf, Double> DOUBLE = StreamCodec.of((buf, value) -> buf.writeDouble(value), ByteBuf::readDouble);
            StreamCodec<ByteBuf, byte[]> BYTE_ARRAY = StreamCodec.of((buf, value) -> {
                VAR_INT.encode(buf, value.length);
                buf.writeBytes(value);
            }, buf -> {
                int count = VAR_INT.decode(buf);
                if (count < 0 || count > buf.readableBytes()) throw new IllegalStateException("Bad byte array length " + count);
                byte[] bytes = new byte[count];
                buf.readBytes(bytes);
                return bytes;
            });
            StreamCodec<ByteBuf, String> STRING_UTF8 = stringUtf8(32767);
            StreamCodec<ByteBuf, Tag> TAG = StreamCodec.of((buf, tag) -> buf.writeBytes(tag.bytes), buf -> {
                int start = buf.readerIndex();
                int type = buf.readByte();
                if (type != 0) skipPayload(buf, type);
                byte[] bytes = new byte[buf.readerIndex() - start];
                buf.getBytes(start, bytes);
                return new Tag(bytes);
            });

            static StreamCodec<ByteBuf, String> stringUtf8(int max) {
                return StreamCodec.of((buf, value) -> {
                    if (value.length() > max) throw new IllegalStateException("String too long");
                    byte[] bytes = value.getBytes(StandardCharsets.UTF_8);
                    VAR_INT.encode(buf, bytes.length);
                    buf.writeBytes(bytes);
                }, buf -> {
                    int count = VAR_INT.decode(buf);
                    if (count < 0 || count > max * 3) throw new IllegalStateException("String too long");
                    byte[] bytes = new byte[count];
                    buf.readBytes(bytes);
                    String value = new String(bytes, StandardCharsets.UTF_8);
                    if (value.length() > max) throw new IllegalStateException("String too long");
                    return value;
                });
            }

            static <B extends ByteBuf, V> StreamCodec<B, Optional<V>> optional(StreamCodec<? super B, V> codec) {
                return StreamCodec.of((buf, value) -> {
                    buf.writeBoolean(value.isPresent());
                    if (value.isPresent()) codec.encode(buf, value.get());
                }, buf -> buf.readBoolean() ? Optional.of(codec.decode(buf)) : Optional.empty());
            }

            static <B extends ByteBuf, V> StreamCodec.CodecOperation<B, V, List<V>> list() {
                return list(Integer.MAX_VALUE);
            }

            static <B extends ByteBuf, V> StreamCodec.CodecOperation<B, V, List<V>> list(int max) {
                return codec -> StreamCodec.of((buf, list) -> {
                    VAR_INT.encode(buf, list.size());
                    for (V value : list) codec.encode(buf, value);
                }, buf -> {
                    int count = VAR_INT.decode(buf);
                    if (count < 0 || count > max) throw new IllegalStateException(count + " elements, more than " + max);
                    List<V> list = new ArrayList<>();
                    for (int i = 0; i < count; i++) list.add(codec.decode(buf));
                    return list;
                });
            }

            private static void writeVarLong(ByteBuf buf, long value) {
                while ((value & ~0x7FL) != 0) {
                    buf.writeByte((int) (value & 0x7F) | 0x80);
                    value >>>= 7;
                }

                buf.writeByte((int) value);
            }

            private static long readVarLong(ByteBuf buf, int maxBytes) {
                long value = 0;
                for (int i = 0; i < maxBytes; i++) {
                    int b = buf.readByte();
                    value |= (long) (b & 0x7F) << (7 * i);
                    if ((b & 0x80) == 0) return value;
                }

                throw new IllegalStateException("VarInt too big");
            }

            private static void skipPayload(ByteBuf buf, int type) {
                switch (type) {
                    case 1: buf.skipBytes(1); break;
                    case 2: buf.skipBytes(2); break;
                    case 3: case 5: buf.skipBytes(4); break;
                    case 4: case 6: buf.skipBytes(8); break;
                    case 7: buf.skipBytes(buf.readInt()); break;
                    case 8: buf.skipBytes(buf.readUnsignedShort()); break;
                    case 9: {
                        int element = buf.readByte();
                        int count = buf.readInt();
                        for (int i = 0; i < count; i++) skipPayload(buf, element);
                        break;
                    }
                    case 10: {
                        int next;
                        while ((next = buf.readByte()) != 0) {
                            buf.skipBytes(buf.readUnsignedShort());
                            skipPayload(buf, next);
                        }
                        break;
                    }
                    case 11: buf.skipBytes(buf.readInt() * 4); break;
                    case 12: buf.skipBytes(buf.readInt() * 8); break;
                    default: throw new IllegalStateException("Bad tag type " + type);
                }
            }
        }
        """;
}
