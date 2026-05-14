using linker.fec;
using System.Buffers.Binary;
using System.Text;


EncodeAndDecode();
await EncodeAndDecodeSticky().ConfigureAwait(false);
Console.ReadLine();

void EncodeAndDecode()
{
    //原始数据
    byte[] source = Encoding.UTF8.GetBytes("hello world!");
    //拼4字节长度前缀
    byte[] rawPacket = new byte[sizeof(int) + source.Length];
    BinaryPrimitives.WriteInt32LittleEndian(rawPacket.AsSpan(0, sizeof(int)), source.Length);
    source.CopyTo(rawPacket.AsSpan(sizeof(int)));

    var options = new LinkerFecOptions { SourceSymbolsPerBlock = 10, RepairSymbolsPerBlock = 2, SymbolSize = 1433 };
    var encodeBuffer = new byte[options.MaxEncodeBufferSize];
    var decodeBuffer = new byte[options.MaxDecodeBufferSize];
    using var encoder = new LinkerFecCodec(options);
    using var decoder = new LinkerFecCodec(options);

    if (encoder.TryEncodePacket(rawPacket, encodeBuffer, out int bytesWritten, out int packetCount))
    {
        var memory = encodeBuffer.AsMemory(0, bytesWritten);
        for (int i = 0; i < packetCount; i++)
        {
            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(memory.Span);
            var frame = memory.Slice(sizeof(int), frameLength);

            if (decoder.TryDecodeFrame(frame, decodeBuffer, out bytesWritten, out var decodedPacketCount))
            {
                var packets = decodeBuffer.AsMemory(0, bytesWritten);
                for (var decodedIndex = 0; decodedIndex < decodedPacketCount; decodedIndex++)
                {
                    var packetLength = BinaryPrimitives.ReadInt32LittleEndian(packets.Span);
                    var packet = packets.Slice(sizeof(int), packetLength);
                    Console.WriteLine($"decoded {Encoding.UTF8.GetString(packet.Span)}");
                    packets = packets.Slice(sizeof(int) + packetLength);

                }
            }

            memory = memory.Slice(4 + frameLength);
        }
    }
}

async Task EncodeAndDecodeSticky()
{
    var options = new LinkerFecOptions { SourceSymbolsPerBlock = 10, RepairSymbolsPerBlock = 2, SymbolSize = 1433 };
    var decodeBuffer = new byte[options.MaxDecodeBufferSize];
    var decoder = new LinkerFecCodec(options);
    StickyPacketEncoder stickyEncoder = new StickyPacketEncoder(256 * 1024, options);
    _ = Task.Run(async () =>
    {
        while (true)
        {
            var memory = await stickyEncoder.ReadAsync().ConfigureAwait(false);
            do
            {
                var frameLength = BinaryPrimitives.ReadInt32LittleEndian(memory.Span);
                var frame = memory.Slice(sizeof(int), frameLength);

                if (decoder.TryDecodeFrame(frame, decodeBuffer, out var bytesWritten, out var decodedPacketCount))
                {
                    var packets = decodeBuffer.AsMemory(0, bytesWritten);
                    do
                    {
                        var packetLength = BinaryPrimitives.ReadInt32LittleEndian(packets.Span);
                        var packet = packets.Slice(sizeof(int), packetLength);
                        Console.WriteLine($"Sticky decoded {Encoding.UTF8.GetString(packet.Span)}");
                        packets = packets.Slice(sizeof(int) + packetLength);

                    } while (packets.Length > 0);
                }

                memory = memory.Slice(sizeof(int) + frameLength);

            } while (memory.Length > 0);
        }
    });

    for (int i = 0; i < 5; i++)
    {
        byte[] source = Encoding.UTF8.GetBytes($"hello world!{i}");
        await stickyEncoder.WriteAsync(source).ConfigureAwait(false);
    }
}