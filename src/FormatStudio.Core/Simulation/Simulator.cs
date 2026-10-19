using System.Text.Json.Nodes;
using FormatStudio.Model;
using FormatStudio.Projects;

namespace FormatStudio.Simulation;

public sealed record RoundTripResult(string Type, JsonNode? Value, byte[] Bytes, byte[]? Reencoded, DiagnosticBag Diagnostics)
{
    public bool Success => !Diagnostics.HasErrors;
}

public sealed record IdentifyMatch(StructDef Type, DecodeResult Result)
{
    /// <summary>Higher is better: decoded without errors, consumed everything, few warnings.</summary>
    public int Score => (Result.Success ? 1000 : 0) + (Result.Success && Result.Root.Length == Result.Consumed && Result.Diagnostics.Count == 0 ? 500 : 0)
        - Result.Diagnostics.Count(d => d.Severity == Severity.Warning) * 10 + Math.Min(Result.Consumed, 400);
}

/// <summary>How a packet id precedes the payload: a primitive, optionally masked (e.g. Bedrock's 10-bit id in a varuint32).</summary>
public sealed record PacketHeader(PrimitiveRef Type, ulong IdMask = ulong.MaxValue);

public static class Simulator
{
    /// <summary>Encodes the value, decodes the bytes and encodes the decoded value again. Both encodings must match.</summary>
    public static RoundTripResult RoundTripValue(Codec codec, TypeRef type, JsonNode? value)
    {
        var diagnostics = new DiagnosticBag();
        var first = codec.Encode(type, value);
        diagnostics.AddRange(first.Diagnostics);
        if (!first.Success) return new RoundTripResult(type.ToString(), value, first.Bytes, null, diagnostics);
        return CompleteRoundTrip(codec, type, value, first.Bytes, diagnostics);
    }

    /// <summary>Decodes the bytes and encodes the result. The encoding must equal the input.</summary>
    public static RoundTripResult RoundTripBytes(Codec codec, TypeRef type, byte[] bytes) =>
        CompleteRoundTrip(codec, type, null, bytes, []);

    private static RoundTripResult CompleteRoundTrip(Codec codec, TypeRef type, JsonNode? value, byte[] bytes, DiagnosticBag diagnostics)
    {
        var decoded = codec.Decode(type, bytes);
        diagnostics.AddRange(decoded.Diagnostics);
        if (!decoded.Success) return new RoundTripResult(type.ToString(), value ?? decoded.Value, bytes, null, diagnostics);
        if (decoded.Consumed != bytes.Length) diagnostics.Error($"Decoding consumed {decoded.Consumed} of {bytes.Length} bytes");

        var second = codec.Encode(type, decoded.Value);
        diagnostics.AddRange(second.Diagnostics);
        if (second.Success && !second.Bytes.AsSpan().SequenceEqual(bytes))
        {
            var mismatch = 0;
            while (mismatch < Math.Min(bytes.Length, second.Bytes.Length) && bytes[mismatch] == second.Bytes[mismatch]) mismatch++;
            diagnostics.Error($"Re-encoding differs at offset {mismatch} ({bytes.Length} vs {second.Bytes.Length} bytes)");
        }

        return new RoundTripResult(type.ToString(), value ?? decoded.Value, bytes, second.Bytes, diagnostics);
    }

    /// <summary>The types fuzzing covers when none are named: the packets, or all structs of a project without packets.</summary>
    public static IEnumerable<StructDef> FuzzTypes(FormatProject project) =>
        project.Packets.Any() ? project.Packets : project.Types.Values.OfType<StructDef>();

    /// <summary>
    /// The seed of the random value of one iteration for one type. Unlike <see cref="HashCode"/>, which differs from
    /// process to process, it is the same every time, so that a failure can be repeated.
    /// </summary>
    public static int FuzzSeed(int seed, string type, int iteration)
    {
        var hash = 2166136261u;
        foreach (var c in $"{seed}/{type}/{iteration}") hash = (hash ^ c) * 16777619u;
        return unchecked((int)hash);
    }

    /// <summary>Round-trips random values of each type.</summary>
    public static IEnumerable<RoundTripResult> Fuzz(FormatProject project, IEnumerable<TypeDef> types, int seed, int iterations)
    {
        var codec = new Codec(project);
        foreach (var definition in types)
        {
            for (var i = 0; i < iterations; i++)
            {
                var type = new NamedRef(definition.Name);
                var random = new RandomValues(project, FuzzSeed(seed, definition.Name, i));
                JsonNode? value = null;
                string? error = null;
                try
                {
                    value = random.Generate(type);
                }
                catch (Exception e) when (e is Expressions.ExprException or FormatDefinitionException)
                {
                    error = e.Message;
                }

                if (error != null)
                {
                    var diagnostics = new DiagnosticBag();
                    diagnostics.Error($"Random value: {error}");
                    yield return new RoundTripResult(definition.Name, null, [], null, diagnostics);
                    continue;
                }

                yield return RoundTripValue(codec, type, value) with { Type = definition.Name };
            }
        }
    }

    /// <summary>Tries the bytes against every packet (or every struct if there are no packets), best match first.</summary>
    public static List<IdentifyMatch> Identify(Codec codec, byte[] bytes, IEnumerable<StructDef>? candidates = null)
    {
        candidates ??= codec.Project.Packets.Any()
            ? codec.Project.Packets
            : codec.Project.Types.Values.OfType<StructDef>();
        return candidates
            .Select(candidate => new IdentifyMatch(candidate, codec.Decode(new NamedRef(candidate.Name), bytes)))
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Type.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads the packet id with <paramref name="header"/> and decodes the rest as the packet with that id.</summary>
    /// <param name="candidates">The packets to choose from, e.g. those of one protocol; all packets by default.</param>
    public static (StructDef? Packet, ulong Id, int HeaderLength, DecodeResult? Result) DecodePacket(Codec codec, byte[] bytes, PacketHeader header,
        IEnumerable<StructDef>? candidates = null)
    {
        var reader = new WireReader(bytes);
        var raw = reader.ReadInteger(header.Type.Kind, header.Type.Endian ?? codec.Project.Defaults.Endian);
        var id = unchecked((ulong)(long)raw) & header.IdMask;
        var packet = (candidates ?? codec.Project.Packets).FirstOrDefault(p => p.Id == (long)id);
        if (packet == null) return (null, id, reader.Position, null);
        return (packet, id, reader.Position, codec.Decode(new NamedRef(packet.Name), bytes[reader.Position..]));
    }

    /// <summary>Writes the packet's id with <paramref name="header"/>, then the packet: what <see cref="DecodePacket"/> reads.</summary>
    public static EncodeResult EncodePacket(Codec codec, StructDef packet, JsonNode? value, PacketHeader header)
    {
        var body = codec.Encode(new NamedRef(packet.Name), value);
        var writer = new WireWriter();
        try
        {
            writer.WriteInteger(header.Type.Kind, header.Type.Endian ?? codec.Project.Defaults.Endian, packet.Id ?? 0);
        }
        catch (EncodeException e)
        {
            body.Diagnostics.Error($"Packet id of {packet.Name}: {e.Message}");
        }

        writer.WriteBytes(body.Bytes);
        return body with { Bytes = writer.ToArray() };
    }
}
