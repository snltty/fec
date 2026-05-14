# linker.fec

这是一个零分配、高性能面向UDP实时传输的前向纠错（FEC）库。它把原始业务包编码成系统源帧和修复帧，接收端在部分 FEC 帧丢失时仍可恢复原始数据，用来降低UDP丢包对业务层的影响。

## 丢包测试

代码在`samples/linker.fec.sample.udp`，以下表格中❌丢失、💚FEC恢复、其它正常

服务端随机丢包30% `iptables -A INPUT -p udp --dport 12345 -m statistic --mode random --probability 0.3 -j DROP` 

### UDP

服务端 `linker.fec.sample.udp.exe server ep0.0.0.0:12345`

客户端 `linker.fec.sample.udp.exe client ep192.168.1.3:12345`

|0|1|2|3|4|5|6|7|8|9|
|---|---|---|---|---|---|---|---|---|---|
|0|❌|❌|3|❌|❌|6|7|❌|9|
|❌|❌|12|13|14|15|❌|17|18|19|
|20|21|❌|❌|❌|25|26|❌|28|❌|
|30|31|32|❌|❌|35|36|37|❌|39|
|40|41|42|43|❌|45|❌|47|48|❌|
|50|51|❌|53|54|55|56|❌|58|59|
|60|61|62|63|64|❌|❌|❌|68|69|
|70|❌|72|❌|74|75|76|77|❌|79|
|80|81|82|83|84|85|86|87|88|89|
|❌|91|92|❌|❌|95|96|❌|❌|99|

### UDP+FEC

服务端 `linker.fec.sample.udp.exe server ep0.0.0.0:12345 fec`

客户端 `linker.fec.sample.udp.exe client ep192.168.1.3:12345 fec`

|0|1|2|3|4|5|6|7|8|9|
|---|---|---|---|---|---|---|---|---|---|
|💚|1|2|3|4|💚|💚|7|8|9|
|10|💚|12|13|14|💚|16|17|💚|💚|
|20|21|💚|23|💚|25|26|27|💚|💚|
|30|31|32|33|34|35|36|37|38|💚|
|40|💚|42|❌|44|45|46|47|48|💚|
|50|51|52|53|54|55|56|💚|58|59|
|60|💚|62|💚|💚|65|66|💚|68|69|
|💚|71|72|73|74|75|💚|77|78|79|
|80|81|💚|83|84|💚|86|87|88|89|
|90|91|92|93|💚|💚|96|97|💚|💚|

## 性能测试

测试环境: C# / .NET 8.0.26, BenchmarkDotNet 0.15.8 ShortRun InProcess；系统 Microsoft Windows 10.0.22631 X64；CPU Intel64 Family 6 Model 158 Stepping 13, GenuineIntel, 16 logical processors；内存 31.93 GiB GC available。默认配置: `SymbolSize=1440`、`SourceSymbolsPerBlock=2`、`RepairSymbolsPerBlock=1`。

### 独立性能encode/decode 

| 操作 | 包长 | 平均耗时 | 吞吐 | 分配 | Gen0 | Gen1 | Gen2 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Encode | 64B | 32.25 ns/op | 15.88 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 64B | 23.96 ns/op | 21.37 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode | 128B | 34.07 ns/op | 30.06 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 128B | 25.83 ns/op | 39.64 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode | 256B | 36.06 ns/op | 56.79 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 256B | 26.58 ns/op | 77.05 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode | 512B | 39.38 ns/op | 104.01 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 512B | 28.20 ns/op | 145.25 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode | 1024B | 52.05 ns/op | 157.39 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 1024B | 31.12 ns/op | 263.24 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode | 1400B | 59.61 ns/op | 187.89 Gbps | 0 B/op | 0 | 0 | 0 |
| Decode | 1400B | 33.70 ns/op | 332.34 Gbps | 0 B/op | 0 | 0 | 0 |

### 整体性能 encode/decode 

| 操作 | 包长 | 平均耗时 | 吞吐 | 分配 | Gen0 | Gen1 | Gen2 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Encode+Decode | 64B | 67.64 ns/op | 7.57 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode+Decode | 128B | 71.16 ns/op | 14.39 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode+Decode | 256B | 72.65 ns/op | 28.19 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode+Decode | 512B | 83.37 ns/op | 49.13 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode+Decode | 1024B | 109.65 ns/op | 74.71 Gbps | 0 B/op | 0 | 0 | 0 |
| Encode+Decode | 1400B | 114.49 ns/op | 97.83 Gbps | 0 B/op | 0 | 0 | 0 |

### 小包合并 encode/decode

带宽比只统计网络发送的 FEC frame 字节，不包含本地 4-byte frame length 前缀。`source frame = 13B header + payload`；`repair frame = 13B header + 2B length symbol + trimmed repair payload`。

| 操作 | 原始包数 | FEC帧数 | 带宽比 |
|---|---:|---:|---:|
| Encode 10/2 64B | 100,000 | 120,002 | 1.45x |
| Decode 10/2 64B | 100,000 | 120,002 | 1.45x |
| Encode 10/2 128B | 100,000 | 120,000 | 1.32x |
| Decode 10/2 128B | 100,000 | 120,000 | 1.32x |
| Encode 10/2 256B | 100,000 | 120,000 | 1.26x |
| Decode 10/2 256B | 100,000 | 120,000 | 1.26x |
| Encode 10/2 512B | 100,000 | 120,000 | 1.23x |
| Decode 10/2 512B | 100,000 | 120,000 | 1.23x |
| Encode 10/2 1024B | 100,000 | 120,000 | 1.22x |
| Decode 10/2 1024B | 100,000 | 120,000 | 1.22x |
| Encode 10/2 1400B | 100,000 | 120,000 | 1.21x |
| Decode 10/2 1400B | 100,000 | 120,000 | 1.21x |

## 基本用法

```
void EncodeAndDecode()
{
    //原始数据
    byte[] source = Encoding.UTF8.GetBytes("hello world!");
    //拼4字节长度前缀
    byte[] rawPacket = new byte[sizeof(int) + source.Length];
    BinaryPrimitives.WriteInt32LittleEndian(rawPacket.AsSpan(0, sizeof(int)), source.Length);
    source.CopyTo(rawPacket.AsSpan(sizeof(int)));

    var options = new LinkerFecOptions { 
        SourceSymbolsPerBlock = 10,
        RepairSymbolsPerBlock = 2, SymbolSize = 1433 
    };
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
    var options = new LinkerFecOptions { 
        SourceSymbolsPerBlock = 10,
        RepairSymbolsPerBlock = 2, 
        SymbolSize = 1433
    };
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
```
