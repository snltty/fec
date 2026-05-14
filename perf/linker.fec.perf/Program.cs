using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using linker.fec;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

Console.WriteLine("# 性能测试");
Console.WriteLine();
EnvironmentReporter.Print();
Console.WriteLine();

var config = ManualConfig
    .Create(DefaultConfig.Instance)
    .AddJob(Job.ShortRun
        .WithId("InProcessShortRun")
        .WithToolchain(InProcessEmitToolchain.Instance))
    .AddDiagnoser(MemoryDiagnoser.Default)
    .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared));

_ = BenchmarkRunner.Run<FecHotPathBenchmarks>(config);

BenchmarkResultReporter.PrintLatest();
await BatchBandwidthReporter.PrintAsync().ConfigureAwait(false);

public class FecHotPathBenchmarks
{
    private const int OperationsPerInvoke = 1024;
    private const int BlockIdOffset = 2;

    private LinkerFecOptions _options = default!;
    private LinkerFecCodec _encodeCodec = default!;
    private LinkerFecCodec _decodeCodec = default!;
    private LinkerFecCodec _roundTripEncodeCodec = default!;
    private LinkerFecCodec _roundTripDecodeCodec = default!;
    private byte[] _record = [];
    private byte[] _encoded = [];
    private byte[] _decoded = [];
    private byte[] _sourceFrame = [];
    private uint _decodeBlockId;

    [Params(64, 128, 256, 512, 1024, 1400)]
    public int PacketLength { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _options = new LinkerFecOptions();
        var packet = DeterministicBytes(PacketLength, 0x5045_5246_0000_0000UL + (uint)PacketLength);
        _record = CreatePacketRecord(packet);
        _encoded = new byte[_options.MaxEncodeBufferSize];
        _decoded = new byte[_options.MaxDecodeBufferSize];
        _encodeCodec = new LinkerFecCodec(_options);
        _decodeCodec = new LinkerFecCodec(_options);
        _roundTripEncodeCodec = new LinkerFecCodec(_options);
        _roundTripDecodeCodec = new LinkerFecCodec(_options);
        _sourceFrame = BuildSourceFrame(_record, _options);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _encodeCodec.Dispose();
        _decodeCodec.Dispose();
        _roundTripEncodeCodec.Dispose();
        _roundTripDecodeCodec.Dispose();
    }

    [Benchmark(Description = "Encode", OperationsPerInvoke = OperationsPerInvoke)]
    public int Encode()
    {
        var checksum = 0;
        for (var i = 0; i < OperationsPerInvoke; i++)
        {
            var bytesWritten = _encodeCodec.EncodePacket(_record.AsSpan(), _encoded.AsSpan(), out var frameCount);
            if (frameCount <= 0 || bytesWritten <= 0)
            {
                throw new InvalidOperationException("Encode did not emit FEC frames.");
            }

            checksum ^= _encoded[sizeof(int) + LinkerFecEncodedSymbol.HeaderSize];
            checksum ^= _encoded[bytesWritten - 1];
        }

        return checksum;
    }

    [Benchmark(Description = "Decode", OperationsPerInvoke = OperationsPerInvoke)]
    public int Decode()
    {
        var checksum = 0;
        for (var i = 0; i < OperationsPerInvoke; i++)
        {
            WriteFrameBlockId(_sourceFrame, _decodeBlockId++);
            if (!_decodeCodec.TryDecodeFrame(_sourceFrame.AsSpan(), _decoded.AsSpan(), out var decodedLength, out var decodedPacketCount))
            {
                throw new InvalidOperationException("Decode did not emit a record list.");
            }

            if (decodedPacketCount != 1)
            {
                throw new InvalidOperationException($"Expected one decoded packet, got {decodedPacketCount}.");
            }

            checksum ^= CountSinglePacketPayloadBytes(_decoded.AsSpan(0, decodedLength), PacketLength);
        }

        return checksum;
    }

    [Benchmark(Description = "Encode+Decode", OperationsPerInvoke = OperationsPerInvoke)]
    public int EncodeDecode()
    {
        var checksum = 0;
        for (var i = 0; i < OperationsPerInvoke; i++)
        {
            var bytesWritten = _roundTripEncodeCodec.EncodePacket(_record.AsSpan(), _encoded.AsSpan(), out _);
            checksum ^= DecodeAllFrames(_encoded.AsSpan(0, bytesWritten), _roundTripDecodeCodec, _decoded, PacketLength);
        }

        return checksum;
    }

    private static byte[] BuildSourceFrame(ReadOnlySpan<byte> record, LinkerFecOptions options)
    {
        var encoded = new byte[options.MaxEncodeBufferSize];
        using var encoder = new LinkerFecCodec(options);
        var bytesWritten = encoder.EncodePacket(record, encoded, out _);
        var sourceFrameLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(0, sizeof(int)));
        if (sourceFrameLength <= 0 || sizeof(int) + sourceFrameLength > bytesWritten)
        {
            throw new InvalidOperationException("Invalid source frame while preparing decode benchmark.");
        }

        return encoded.AsSpan(sizeof(int), sourceFrameLength).ToArray();
    }

    private static void WriteFrameBlockId(byte[] frame, uint blockId)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(BlockIdOffset, sizeof(uint)), blockId);
    }

    private static int DecodeAllFrames(
        ReadOnlySpan<byte> encodedPacket,
        LinkerFecCodec decoder,
        byte[] decoded,
        int packetLength)
    {
        var checksum = 0;
        var offset = 0;
        var emitted = false;
        while (offset < encodedPacket.Length)
        {
            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(encodedPacket.Slice(offset, sizeof(int)));
            offset += sizeof(int);
            if (decoder.TryDecodeFrame(encodedPacket.Slice(offset, frameLength), decoded.AsSpan(), out var decodedLength, out var decodedPacketCount))
            {
                if (decodedPacketCount != 1)
                {
                    throw new InvalidOperationException($"Expected one decoded packet, got {decodedPacketCount}.");
                }

                checksum ^= CountSinglePacketPayloadBytes(decoded.AsSpan(0, decodedLength), packetLength);
                emitted = true;
            }

            offset += frameLength;
        }

        return emitted
            ? checksum
            : throw new InvalidOperationException("Round-trip decode did not emit a record list.");
    }

    private static int CountSinglePacketPayloadBytes(ReadOnlySpan<byte> records, int expectedPacketLength)
    {
        if (records.Length != sizeof(int) + expectedPacketLength)
        {
            throw new InvalidOperationException("Decoded record length mismatch.");
        }

        var packetLength = BinaryPrimitives.ReadInt32LittleEndian(records[..sizeof(int)]);
        if (packetLength != expectedPacketLength)
        {
            throw new InvalidOperationException($"Decoded packet length mismatch: expected {expectedPacketLength}, got {packetLength}.");
        }

        var payload = records.Slice(sizeof(int), packetLength);
        return payload[0] ^ payload[^1];
    }

    private static byte[] CreatePacketRecord(ReadOnlySpan<byte> packet)
    {
        var record = new byte[sizeof(int) + packet.Length];
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, sizeof(int)), packet.Length);
        packet.CopyTo(record.AsSpan(sizeof(int)));
        return record;
    }

    private static byte[] DeterministicBytes(int length, ulong seed)
    {
        var data = new byte[length];
        new FastRandom(seed).NextBytes(data);
        return data;
    }
}

internal static class BenchmarkResultReporter
{
    private static readonly string[] IndependentMethods = ["Encode", "Decode"];

    public static void PrintLatest()
    {
        var csvPath = FindLatestCsvReport();
        var results = ReadResults(csvPath);

        Console.WriteLine("## encode/decode 独立性能");
        Console.WriteLine();
        PrintBenchmarkTable(results
            .Where(static result => IndependentMethods.Contains(result.Operation, StringComparer.Ordinal))
            .OrderBy(static result => result.PacketLength)
            .ThenBy(static result => result.Operation == "Encode" ? 0 : 1));

        Console.WriteLine();
        Console.WriteLine("## encode decode 整体性能");
        Console.WriteLine();
        PrintBenchmarkTable(results
            .Where(static result => result.Operation == "Encode+Decode")
            .OrderBy(static result => result.PacketLength));
        Console.WriteLine();
    }

    private static void PrintBenchmarkTable(IEnumerable<BenchmarkResult> rows)
    {
        Console.WriteLine("| 操作 | 包长 | 平均耗时 | 吞吐 | 分配 | Gen0 | Gen1 | Gen2 |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in rows)
        {
            Console.WriteLine(
                $"| {row.Operation} | {row.PacketLength}B | {row.Mean} | " +
                $"{FormatThroughput(row.PacketLength, row.MeanNanoseconds)} | {row.Allocated} | " +
                $"{row.Gen0} | {row.Gen1} | {row.Gen2} |");
        }
    }

    private static string FindLatestCsvReport()
    {
        var directPath = Path.Combine(
            Environment.CurrentDirectory,
            "BenchmarkDotNet.Artifacts",
            "results",
            $"{nameof(FecHotPathBenchmarks)}-report.csv");
        if (File.Exists(directPath))
        {
            return directPath;
        }

        return Directory
            .EnumerateFiles(Environment.CurrentDirectory, $"{nameof(FecHotPathBenchmarks)}-report.csv", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new FileNotFoundException("BenchmarkDotNet CSV report was not found.");
    }

    private static List<BenchmarkResult> ReadResults(string csvPath)
    {
        var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
        if (lines.Length < 2)
        {
            throw new InvalidDataException("BenchmarkDotNet CSV report does not contain benchmark rows.");
        }

        var headers = SplitCsvLine(lines[0]);
        var methodIndex = RequiredIndex(headers, "Method");
        var packetLengthIndex = RequiredIndex(headers, "PacketLength");
        var meanIndex = RequiredIndex(headers, "Mean");
        var allocatedIndex = OptionalIndex(headers, "Allocated");
        var gen0Index = OptionalIndex(headers, "Gen0", "Gen 0");
        var gen1Index = OptionalIndex(headers, "Gen1", "Gen 1");
        var gen2Index = OptionalIndex(headers, "Gen2", "Gen 2");

        var results = new List<BenchmarkResult>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var fields = SplitCsvLine(lines[i]);
            var mean = fields[meanIndex].Trim();
            results.Add(new BenchmarkResult(
                fields[methodIndex].Trim(),
                int.Parse(fields[packetLengthIndex], CultureInfo.InvariantCulture),
                mean,
                ParseDurationToNanoseconds(mean),
                FormatAllocation(allocatedIndex >= 0 ? fields[allocatedIndex].Trim() : "0 B"),
                FormatGeneration(gen0Index >= 0 ? fields[gen0Index].Trim() : "0"),
                FormatGeneration(gen1Index >= 0 ? fields[gen1Index].Trim() : "0"),
                FormatGeneration(gen2Index >= 0 ? fields[gen2Index].Trim() : "0")));
        }

        return results;
    }

    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }

                continue;
            }

            if (ch == ',' && !quoted)
            {
                fields.Add(value.ToString());
                value.Clear();
                continue;
            }

            value.Append(ch);
        }

        fields.Add(value.ToString());
        return fields.ToArray();
    }

    private static int RequiredIndex(string[] headers, string name)
    {
        var index = OptionalIndex(headers, name);
        return index >= 0
            ? index
            : throw new InvalidDataException($"BenchmarkDotNet CSV report is missing '{name}' column.");
    }

    private static int OptionalIndex(string[] headers, params string[] names)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            foreach (var name in names)
            {
                if (string.Equals(headers[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static double ParseDurationToNanoseconds(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new FormatException("Benchmark duration is empty.");
        }

        var amount = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var unit = parts.Length > 1 ? parts[1] : "ns";
        return unit switch
        {
            "ns" => amount,
            "us" or "µs" => amount * 1_000,
            "ms" => amount * 1_000_000,
            "s" => amount * 1_000_000_000,
            _ => throw new FormatException($"Unsupported benchmark duration unit '{unit}'.")
        };
    }

    private static string FormatThroughput(int packetLength, double meanNanoseconds)
    {
        var gbps = packetLength * 8.0 / meanNanoseconds;
        return $"{gbps:N2} Gbps";
    }

    private static string FormatAllocation(string allocated)
    {
        if (string.IsNullOrWhiteSpace(allocated) || allocated == "-")
        {
            return "0 B/op";
        }

        return allocated.EndsWith("/op", StringComparison.Ordinal)
            ? allocated
            : $"{allocated}/op";
    }

    private static string FormatGeneration(string value)
    {
        return string.IsNullOrWhiteSpace(value) || value == "-"
            ? "0"
            : value;
    }

    private sealed record BenchmarkResult(
        string Operation,
        int PacketLength,
        string Mean,
        double MeanNanoseconds,
        string Allocated,
        string Gen0,
        string Gen1,
        string Gen2);
}

internal static class BatchBandwidthReporter
{
    private const int BatchPacketCount = 100_000;
    private const int BatchMaxRemaining = 4 * 1024 * 1024;
    private static readonly int[] PacketLengths = [64, 128, 256, 512, 1024, 1400];

    public static async Task PrintAsync()
    {
        var options = new LinkerFecOptions
        {
            SourceSymbolsPerBlock = 10,
            RepairSymbolsPerBlock = 2
        };

        Console.WriteLine("## 包批处理 encode/decode");
        Console.WriteLine();
        Console.WriteLine($"源包: {BatchPacketCount:N0}, 配置: 10/2, packet: 64/128/256/512/1024/1400 bytes");
        Console.WriteLine("带宽比只统计网络发送的 FEC frame 字节，不包含本地 4-byte frame length 前缀。");
        Console.WriteLine("source frame = 13B header + payload；repair frame = 13B header + 2B length symbol + trimmed repair payload。");
        Console.WriteLine();
        Console.WriteLine("| 操作 | 原始包数 | FEC帧数 | 带宽比 |");
        Console.WriteLine("|---|---:|---:|---:|");

        foreach (var packetLength in PacketLengths)
        {
            var packets = CreatePackets(BatchPacketCount, packetLength, 0x4241_5443_4800_0000UL + (uint)packetLength);
            var encoded = await EncodePacketBatchesAsync(packets, options).ConfigureAwait(false);
            var decoded = DecodePacketBatches(encoded.EncodedPackets, options, packetLength);
            if (decoded.PacketCount != BatchPacketCount)
            {
                throw new InvalidOperationException(
                    $"Batch decoded packet count mismatch: expected {BatchPacketCount}, got {decoded.PacketCount}.");
            }

            PrintBatchRow("Encode", packetLength, encoded.Stats);
            PrintBatchRow("Decode", packetLength, encoded.Stats with { PacketCount = decoded.PacketCount });
        }

        Console.WriteLine();
    }

    private static async Task<BatchEncodedData> EncodePacketBatchesAsync(
        IReadOnlyList<byte[]> packets,
        LinkerFecOptions options)
    {
        var encodedPackets = new List<byte[]>();
        var expectedBatchBytes = packets.Sum(static packet => packet.Length + sizeof(int));
        var appBytes = 0L;
        var batchBytes = 0L;
        var encodedBytes = 0L;
        var fecFrameCount = 0;
        var checksum = 0;

        using var encoder = new LinkerFecPacketBatcher(BatchMaxRemaining, options);
        var consumer = Task.Run(async () =>
        {
            while (batchBytes < expectedBatchBytes)
            {
                var encoded = await encoder.ReadAsync().ConfigureAwait(false);
                if (encoded.IsEmpty)
                {
                    throw new InvalidOperationException("Packet batcher completed before all packets were encoded.");
                }

                batchBytes += encoder.LastRawBytes;
                fecFrameCount += encoder.LastEncodedFrameCount;
                encodedBytes += CountNetworkFrameBytes(encoded.Span);
                checksum ^= encoded.Span[0];
                checksum ^= encoded.Span[^1];
                encodedPackets.Add(encoded.ToArray());
            }
        });

        foreach (var packet in packets)
        {
            await encoder.WriteAsync(packet).ConfigureAwait(false);
            appBytes += packet.Length;
        }

        encoder.Complete();
        await consumer.ConfigureAwait(false);

        if (batchBytes != expectedBatchBytes)
        {
            throw new InvalidOperationException("Packet batcher consumed an unexpected number of record bytes.");
        }

        Consume(checksum);
        return new BatchEncodedData(
            encodedPackets.ToArray(),
            new BatchStats(packets.Count, fecFrameCount, appBytes, encodedBytes));
    }

    private static BatchStats DecodePacketBatches(
        IReadOnlyList<byte[]> encodedPackets,
        LinkerFecOptions options,
        int expectedPacketLength)
    {
        var packetCount = 0;
        var appBytes = 0L;
        var checksum = 0;
        var decoded = new byte[options.MaxDecodeBufferSize];

        using var decoder = new LinkerFecCodec(options);
        foreach (var encodedPacket in encodedPackets)
        {
            var offset = 0;
            while (offset < encodedPacket.Length)
            {
                if (encodedPacket.Length - offset < sizeof(int))
                {
                    throw new InvalidOperationException("Batched encoded packet ended inside a frame length prefix.");
                }

                var frameLength = BinaryPrimitives.ReadInt32LittleEndian(encodedPacket.AsSpan(offset, sizeof(int)));
                offset += sizeof(int);
                if (frameLength <= 0 || frameLength > encodedPacket.Length - offset)
                {
                    throw new InvalidOperationException($"Invalid batched FEC frame length {frameLength}.");
                }

                if (decoder.TryDecodeFrame(encodedPacket.AsSpan(offset, frameLength), decoded.AsSpan(), out var decodedLength, out var decodedPacketCount))
                {
                    CountBatchRecords(
                        decoded.AsSpan(0, decodedLength),
                        decodedPacketCount,
                        expectedPacketLength,
                        ref packetCount,
                        ref appBytes,
                        ref checksum);
                }

                offset += frameLength;
            }
        }

        Consume(checksum);
        return new BatchStats(packetCount, 0, appBytes, 0);
    }

    private static void CountBatchRecords(
        ReadOnlySpan<byte> records,
        int decodedPacketCount,
        int expectedPacketLength,
        ref int packetCount,
        ref long appBytes,
        ref int checksum)
    {
        for (var i = 0; i < decodedPacketCount; i++)
        {
            if (records.Length < sizeof(int))
            {
                throw new InvalidOperationException("Batched record list ended inside a length prefix.");
            }

            var packetLength = BinaryPrimitives.ReadInt32LittleEndian(records[..sizeof(int)]);
            records = records[sizeof(int)..];
            if (packetLength != expectedPacketLength || packetLength > records.Length)
            {
                throw new InvalidOperationException($"Invalid batched packet length {packetLength}.");
            }

            checksum ^= records[0];
            checksum ^= records[packetLength - 1];
            packetCount++;
            appBytes += packetLength;
            records = records[packetLength..];
        }
    }

    private static long CountNetworkFrameBytes(ReadOnlySpan<byte> encodedPacket)
    {
        var offset = 0;
        var bytes = 0L;
        while (offset < encodedPacket.Length)
        {
            if (encodedPacket.Length - offset < sizeof(int))
            {
                throw new InvalidOperationException("Encoded packet ended inside a frame length prefix.");
            }

            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(encodedPacket.Slice(offset, sizeof(int)));
            offset += sizeof(int);
            if (frameLength <= 0 || frameLength > encodedPacket.Length - offset)
            {
                throw new InvalidOperationException($"Invalid FEC frame length {frameLength}.");
            }

            bytes += frameLength;
            offset += frameLength;
        }

        return bytes;
    }

    private static byte[][] CreatePackets(int count, int packetLength, ulong seed)
    {
        var packets = new byte[count][];
        var random = new FastRandom(seed);
        for (var i = 0; i < packets.Length; i++)
        {
            packets[i] = new byte[packetLength];
            random.NextBytes(packets[i]);
        }

        return packets;
    }

    private static void PrintBatchRow(string operation, int packetLength, BatchStats stats)
    {
        Console.WriteLine(
            $"| {operation} 10/2 {packetLength}B | {stats.PacketCount:N0} | {stats.FecFrameCount:N0} | " +
            $"{stats.EncodedBytes / (double)stats.AppBytes:N2}x |");
    }

    private static void Consume(int value)
    {
        if (value == int.MinValue)
        {
            throw new InvalidOperationException("Unreachable checksum guard.");
        }
    }

    private sealed record BatchEncodedData(byte[][] EncodedPackets, BatchStats Stats);

    private readonly record struct BatchStats(
        int PacketCount,
        int FecFrameCount,
        long AppBytes,
        long EncodedBytes);
}

internal static class EnvironmentReporter
{
    public static void Print()
    {
        var options = new LinkerFecOptions();
        Console.WriteLine("## 测试环境");
        Console.WriteLine();
        Console.WriteLine($"测试时间: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"语言/运行时: C# / {RuntimeInformation.FrameworkDescription}, CLR {Environment.Version}");
        Console.WriteLine($"系统: {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"CPU: {GetCpuDescription()}");
        Console.WriteLine($"内存: {FormatBytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)} GC available");
        Console.WriteLine(
            $"默认配置: SymbolSize={options.SymbolSize}, SourceSymbolsPerBlock={options.SourceSymbolsPerBlock}, " +
            $"RepairSymbolsPerBlock={options.RepairSymbolsPerBlock}, MaxDecoderBlocks={options.MaxDecoderBlocks}, " +
            $"MaxSkipBlocks={options.MaxSkipBlocks}");
    }

    private static string GetCpuDescription()
    {
        var processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return string.IsNullOrWhiteSpace(processor)
            ? $"{Environment.ProcessorCount} logical processors"
            : $"{processor}, {Environment.ProcessorCount} logical processors";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "unknown";
        }

        var gib = bytes / 1024.0 / 1024 / 1024;
        return $"{gib:N2} GiB";
    }
}

internal struct FastRandom
{
    private ulong _state;

    public FastRandom(ulong seed)
    {
        _state = seed == 0 ? 0x9E37_79B9_7F4A_7C15UL : seed;
    }

    public void NextBytes(Span<byte> destination)
    {
        while (destination.Length >= sizeof(ulong))
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination, NextUInt64());
            destination = destination[sizeof(ulong)..];
        }

        if (destination.Length == 0)
        {
            return;
        }

        var value = NextUInt64();
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = (byte)(value >> (i * 8));
        }
    }

    private ulong NextUInt64()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        _state = x;
        return x;
    }
}
