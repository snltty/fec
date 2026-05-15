using System.Buffers.Binary;
using System.Text;
using linker.fec;

EncodeAndDecode();
EncodeAndDecodeStreaming();
Console.ReadLine();

void EncodeAndDecode()
{
    byte[] source = Encoding.UTF8.GetBytes("hello world!");
    byte[] rawPacket = new byte[sizeof(int) + source.Length];
    BinaryPrimitives.WriteInt32LittleEndian(rawPacket.AsSpan(0, sizeof(int)), source.Length);
    source.CopyTo(rawPacket.AsSpan(sizeof(int)));

    var options = new LinkerFecOptions
    {
        SourceSymbolsPerBlock = 10,
        RepairSymbolsPerBlock = 2,
        SymbolSize = 1433
    };
    var encodeBuffer = new byte[options.MaxEncodeBufferSize];
    var decodeBuffer = new byte[options.MaxDecodeBufferSize];
    using var encoder = new LinkerFecCodec(options);
    using var decoder = new LinkerFecCodec(options);

    if (encoder.TryEncodePacket(rawPacket, encodeBuffer, out int bytesWritten, out int packetCount))
    {
        DecodeLengthPrefixedFrames(encodeBuffer.AsMemory(0, bytesWritten), decoder, decodeBuffer, "decoded");
    }
}

void EncodeAndDecodeStreaming()
{
    var options = new LinkerFecOptions
    {
        SourceSymbolsPerBlock = 3,
        RepairSymbolsPerBlock = 2,
        SymbolSize = 1433
    };
    var encodeBuffer = new byte[options.MaxEncodeBufferSize];
    var decodeBuffer = new byte[options.MaxDecodeBufferSize];
    using var encoder = new LinkerFecStreamingEncoder(options);
    using var decoder = new LinkerFecCodec(options);

    for (var i = 0; i < options.SourceSymbolsPerBlock; i++)
    {
        byte[] source = Encoding.UTF8.GetBytes($"hello streaming!{i}");
        byte[] rawPacket = new byte[sizeof(int) + source.Length];
        BinaryPrimitives.WriteInt32LittleEndian(rawPacket.AsSpan(0, sizeof(int)), source.Length);
        source.CopyTo(rawPacket.AsSpan(sizeof(int)));

        var bytesWritten = encoder.EncodePacket(
            rawPacket,
            encodeBuffer,
            out _,
            isFinalPacket: i == options.SourceSymbolsPerBlock - 1);
        DecodeLengthPrefixedFrames(encodeBuffer.AsMemory(0, bytesWritten), decoder, decodeBuffer, "streaming decoded");
    }
}

void DecodeLengthPrefixedFrames(
    ReadOnlyMemory<byte> encoded,
    LinkerFecCodec decoder,
    byte[] decodeBuffer,
    string prefix)
{
    var memory = encoded;
    while (!memory.IsEmpty)
    {
        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(memory.Span);
        var frame = memory.Slice(sizeof(int), frameLength);

        if (decoder.TryDecodeFrame(frame, decodeBuffer, out var bytesWritten, out var decodedPacketCount))
        {
            var packets = decodeBuffer.AsMemory(0, bytesWritten);
            for (var decodedIndex = 0; decodedIndex < decodedPacketCount; decodedIndex++)
            {
                var packetLength = BinaryPrimitives.ReadInt32LittleEndian(packets.Span);
                var packet = packets.Slice(sizeof(int), packetLength);
                Console.WriteLine($"{prefix} {Encoding.UTF8.GetString(packet.Span)}");
                packets = packets.Slice(sizeof(int) + packetLength);
            }
        }

        memory = memory.Slice(sizeof(int) + frameLength);
    }
}
