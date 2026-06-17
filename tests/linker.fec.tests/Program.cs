using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using linker.fec;

if (args.Length > 0 && string.Equals(args[0], "--single-repair-throughput", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = RunSingleRepairThroughput(args);
    return;
}

var tests = new (string Name, Func<Task> Body)[]
{
    ("round trip without loss", RoundTripWithoutLoss),
    ("round trip with recoverable erasures", RoundTripWithRecoverableErasures),
    ("repair-only reference decode", RepairOnlyReferenceDecode),
    ("external destination decode", ExternalDestinationDecode),
    ("packetized sync encode decode", PacketizedSyncEncodeDecode),
    ("try decode frame returns packet count", TryDecodeFrameReturnsPacketCount),
    ("fec recovered packet count tracks only repair recoveries", FecRecoveredPacketCountTracksOnlyRepairRecoveries),
    ("decoded packet kinds track each output record", DecodedPacketKindsTrackEachOutputRecord),
    ("record length prefix does not consume symbol capacity", RecordLengthPrefixDoesNotConsumeSymbolCapacity),
    ("repair profile chooses absolute repair symbols", RepairProfileChoosesAbsoluteRepairSymbols),
    ("repair profile interpolates missing source counts", RepairProfileInterpolatesMissingSourceCounts),
    ("single source profile emits multiple repair symbols", SingleSourceProfileEmitsMultipleRepairSymbols),
    ("single source repair frame is trimmed", SingleSourceRepairFrameIsTrimmed),
    ("intermediate repair generation matches direct repair", IntermediateRepairGenerationMatchesDirectRepair),
    ("packetized frame length keeps 1400 byte frames bounded", PacketizedFrameLengthKeeps1400ByteFramesBounded),
    ("compact block id wraps across uint boundary", CompactBlockIdWrapsAcrossUIntBoundary),
    ("max skip blocks skips unrecoverable gap", MaxSkipBlocksSkipsUnrecoverableGap),
    ("max skip blocks keeps recent late packets", MaxSkipBlocksKeepsRecentLatePackets),
    ("serialized frames round trip", SerializedFramesRoundTrip),
    ("corrupt frame is rejected", CorruptFrameIsRejected),
    ("too much loss emits only received source data", TooMuchLossDoesNotEmitRecoveredData),
    ("empty input round trip", EmptyInputRoundTrip),
    ("invalid application records are rejected", InvalidApplicationRecordsAreRejected),
    ("invalid options are rejected", InvalidOptionsAreRejected)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Body();
        Console.WriteLine($"[PASS] {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"[FAIL] {test.Name}: {ex}");
    }
}

if (failed > 0)
{
    Environment.ExitCode = 1;
}

static int RunSingleRepairThroughput(string[] args)
{
    var seconds = GetOptionInt(args, "--seconds", GetIntArg(args, 1, 10));
    var payloadLength = GetOptionInt(args, "--payload", 1400);
    if (seconds <= 0)
    {
        Console.Error.WriteLine("seconds must be greater than zero.");
        return 1;
    }

    if (payloadLength is <= 0 or > LinkerFecOptions.MaxRecordPayloadLength)
    {
        Console.Error.WriteLine($"payload must be in [1, {LinkerFecOptions.MaxRecordPayloadLength}].");
        return 1;
    }

    var options = new LinkerFecOptions
    {
        SymbolSize = Math.Max(payloadLength, LinkerFecOptions.MinSymbolSize),
        SourceSymbolsPerBlock = 1,
        RepairSymbolsPerBlock = 1,
        MaxDecoderBlocks = 1024,
        MaxSkipBlocks = 1024
    };

    Console.WriteLine(
        $"fec single-repair throughput: seconds={seconds}, payload={payloadLength}B, symbol={options.SymbolSize}B, source=1, repair=1, sourceLoss=100%");

    _ = RunSingleRepairThroughputCore(TimeSpan.FromSeconds(1), payloadLength, options);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var result = RunSingleRepairThroughputCore(TimeSpan.FromSeconds(seconds), payloadLength, options);
    var appGbps = result.Elapsed.TotalSeconds <= 0
        ? 0
        : result.AppBytes * 8.0 / result.Elapsed.TotalSeconds / 1_000_000_000.0;
    var encodedGbps = result.Elapsed.TotalSeconds <= 0
        ? 0
        : result.EncodedBytes * 8.0 / result.Elapsed.TotalSeconds / 1_000_000_000.0;
    var blocksPerSecond = result.Elapsed.TotalSeconds <= 0 ? 0 : result.Blocks / result.Elapsed.TotalSeconds;

    Console.WriteLine(
        $"FEC 1+1 repair-only encode+decode: app={appGbps:N2} Gbps, encoded={encodedGbps:N2} Gbps, blocks={result.Blocks:N0}, blocks/s={blocksPerSecond:N0}, dt={result.Elapsed.TotalSeconds:N2}s");
    Console.WriteLine(
        $"FEC 1+1 repair-only frames: packets/block={result.PacketCount}, sourceFrame={result.SourceFrameLength}B, repairFrame={result.RepairFrameLength}B");
    return 0;
}

static (long Blocks, long AppBytes, long EncodedBytes, TimeSpan Elapsed, int PacketCount, int SourceFrameLength, int RepairFrameLength)
    RunSingleRepairThroughputCore(TimeSpan duration, int payloadLength, LinkerFecOptions options)
{
    var record = CreatePacketRecord(DeterministicBytes(payloadLength));
    var encoded = new byte[options.MaxEncodeBufferSize];
    var decoded = new byte[options.MaxDecodeBufferSize];
    using var encoder = new LinkerFecCodec(options);
    using var decoder = new LinkerFecCodec(options);

    long blocks = 0;
    long appBytes = 0;
    long encodedBytes = 0;
    var packetCount = 0;
    var sourceFrameLength = 0;
    var repairFrameLength = 0;
    var start = Stopwatch.GetTimestamp();

    while (true)
    {
        record[LinkerFecOptions.RecordLengthPrefixSize] = unchecked((byte)blocks);
        record[^1] = unchecked((byte)(blocks >> 8));

        var bytesWritten = encoder.EncodePacket(record.AsSpan(), encoded.AsSpan(), out packetCount);
        sourceFrameLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(0, LinkerFecOptions.FrameLengthPrefixSize));
        var repairFrameOffset = LinkerFecOptions.FrameLengthPrefixSize + sourceFrameLength;
        repairFrameLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(repairFrameOffset, LinkerFecOptions.FrameLengthPrefixSize));
        var repairFrame = encoded.AsSpan(repairFrameOffset + LinkerFecOptions.FrameLengthPrefixSize, repairFrameLength);

        if (!decoder.TryDecodeFrame(repairFrame, decoded.AsSpan(), out var decodedLength, out var decodedPacketCount))
        {
            throw new InvalidOperationException("Repair-only decode did not recover the source packet.");
        }

        if (decodedLength != record.Length || decodedPacketCount != 1)
        {
            throw new InvalidOperationException("Repair-only decode returned an unexpected record length or packet count.");
        }

        if (blocks == 0 && !decoded.AsSpan(0, decodedLength).SequenceEqual(record))
        {
            throw new InvalidOperationException("Repair-only decode payload mismatch.");
        }

        blocks++;
        appBytes += payloadLength;
        encodedBytes += bytesWritten;

        if ((blocks & 0x3FFF) == 0 && Stopwatch.GetElapsedTime(start) >= duration)
        {
            break;
        }
    }

    return (blocks, appBytes, encodedBytes, Stopwatch.GetElapsedTime(start), packetCount, sourceFrameLength, repairFrameLength);
}

static Task RoundTripWithoutLoss()
{
    var options = TestOptions();
    var raw = DeterministicBytes(31_337);
    var frames = EncodeToFrames(raw, options);
    var decoded = DecodeFrames(frames, options);
    AssertEqual(raw, decoded);
    return Task.CompletedTask;
}

static Task RoundTripWithRecoverableErasures()
{
    var options = TestOptions();
    var raw = DeterministicBytes(40_123);
    var frames = EncodeToFrames(raw, options);
    var transmitted = DropSourceSymbols(frames, options, 0, 2, 4);
    var decoded = DecodeFrames(transmitted, options);
    AssertEqual(raw, decoded);
    return Task.CompletedTask;
}

static Task SerializedFramesRoundTrip()
{
    var options = TestOptions();
    var raw = DeterministicBytes(7777);
    var frames = EncodeToFrames(raw, options);
    var decoded = DecodeFrames(frames.Select(f => LinkerFecEncodedSymbol.Parse(f, options).ToArray()), options);
    AssertEqual(raw, decoded);
    return Task.CompletedTask;
}

static Task RepairOnlyReferenceDecode()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 10,
        RepairSymbolsPerBlock = 12
    };
    var packets = Enumerable.Range(0, options.SourceSymbolsPerBlock)
        .Select(i => DeterministicBytes(20 + i))
        .ToArray();
    var records = CreatePacketRecords(packets);
    var frames = EncodeRecordListToFrames(records, options);
    var repairsOnly = frames
        .Select(frame => LinkerFecEncodedSymbol.Parse(frame, options))
        .Where(symbol => symbol.IsRepair)
        .Select(symbol => symbol.ToArray())
        .ToList();

    var decoded = DecodeFrames(repairsOnly, options);
    AssertEqual(ConcatPackets(packets), decoded);
    return Task.CompletedTask;
}

static Task ExternalDestinationDecode()
{
    var options = TestOptions();
    var raw = DeterministicBytes(12_345);
    var frames = EncodeToFrames(raw, options);
    var transmitted = DropSourceSymbols(frames, options, 1, 3);

    var dst = new byte[options.MaxDecodeBufferSize];
    using var output = new MemoryStream();
    var decoder = new LinkerFecCodec(options);
    try
    {
        foreach (var frame in transmitted)
        {
            var length = decoder.DecodeFrame(frame.AsSpan(), dst.AsSpan());
            if (length > 0)
            {
                WritePacketRecordsPayloads(dst.AsSpan(0, length), output);
            }
        }
    }
    finally
    {
        decoder.Dispose();
    }

    AssertEqual(raw, output.ToArray());
    return Task.CompletedTask;
}

static Task PacketizedSyncEncodeDecode()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 600,
        SourceSymbolsPerBlock = 4,
        RepairSymbolsPerBlock = 3,
        MaxDecoderBlocks = 256
    };
    var packets = new[]
    {
        DeterministicBytes(150),
        DeterministicBytes(175),
        DeterministicBytes(200),
        DeterministicBytes(225)
    };
    var record = CreatePacketRecords(packets);

    var encoder = new LinkerFecCodec(options);
    var encoded = new byte[GetEncodedPacketSize(record.Length, options)];
    var bytesWritten = encoder.EncodePacket(record.AsSpan(), encoded.AsSpan(), out var packetCount, isFinalPacket: true);
    encoder.Dispose();

    Assert(bytesWritten <= encoded.Length, "Packetized encoder wrote past the destination buffer.");
    Assert(packetCount == 7, $"Expected 7 encoded packets, got {packetCount}.");

    var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];
    using var decodedRecords = new MemoryStream();
    try
    {
        var offset = 0;
        while (offset < bytesWritten)
        {
            var frameLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(offset, LinkerFecOptions.FrameLengthPrefixSize));
            offset += LinkerFecOptions.FrameLengthPrefixSize;
            var length = decoder.DecodeFrame(encoded.AsSpan(offset, frameLength), decoded);
            if (length > 0)
            {
                decodedRecords.Write(decoded.AsSpan(0, length));
            }

            offset += frameLength;
        }
    }
    finally
    {
        decoder.Dispose();
    }

    var decodedRecordList = decodedRecords.ToArray();
    Assert(decodedRecordList.Length == record.Length, "Decoder did not emit packetized record data.");
    Assert(record.AsSpan().SequenceEqual(decodedRecordList), "Decoded packetized record data differs from input.");
    AssertPacketSequence(packets, ParsePacketRecords(decodedRecordList));
    return Task.CompletedTask;
}

static Task TryDecodeFrameReturnsPacketCount()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 3,
        RepairSymbolsPerBlock = 1
    };
    var packets = new[]
    {
        DeterministicBytes(17),
        DeterministicBytes(31),
        DeterministicBytes(43)
    };
    var records = CreatePacketRecords(packets);
    var frames = EncodeRecordListToFrames(records, options);
    using var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];
    var decodedPackets = new List<byte[]>();
    var successes = 0;

    foreach (var frame in frames)
    {
        if (!decoder.TryDecodeFrame(frame.AsSpan(), decoded.AsSpan(), out var decodedLength, out var decodedPacketCount))
        {
            Assert(decodedLength == 0, "Incomplete decode should not report decoded bytes.");
            Assert(decodedPacketCount == 0, "Incomplete decode should not report decoded packets.");
            continue;
        }

        successes++;
        var emittedPackets = ParsePacketRecords(decoded.AsMemory(0, decodedLength));
        Assert(decodedPacketCount == emittedPackets.Count,
            $"Expected packetCount {emittedPackets.Count}, got {decodedPacketCount}.");
        decodedPackets.AddRange(emittedPackets);
    }

    Assert(successes == packets.Length, $"Expected one successful decode event per source packet, got {successes}.");
    AssertPacketSequence(packets, decodedPackets);
    return Task.CompletedTask;
}

static Task FecRecoveredPacketCountTracksOnlyRepairRecoveries()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 4,
        RepairSymbolsPerBlock = 2
    };
    var packets = new[]
    {
        DeterministicBytes(50),
        DeterministicBytes(52),
        DeterministicBytes(54),
        DeterministicBytes(56)
    };
    var frames = EncodeRecordListToFrames(CreatePacketRecords(packets), options)
        .Select(frame => LinkerFecEncodedSymbol.Parse(frame, options))
        .ToArray();
    using var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];
    var directPackets = new List<byte[]>();
    var recoveredPackets = new List<byte[]>();

    foreach (var symbol in frames.Where(symbol => !symbol.IsRepair && symbol.SymbolId != 1))
    {
        var frame = symbol.ToArray();
        Assert(
            decoder.TryDecodeFrame(frame.AsSpan(), decoded.AsSpan(), out var decodedLength, out var decodedPacketCount),
            "Received source frame should be emitted directly.");
        Assert(decodedPacketCount == 1, $"Expected one direct source packet, got {decodedPacketCount}.");
        directPackets.AddRange(ParsePacketRecords(decoded.AsMemory(0, decodedLength)));
        Assert(decoder.FecRecoveredPacketCount == 0, "Direct source output should not increment FEC recovery count.");
    }

    foreach (var symbol in frames.Where(symbol => symbol.IsRepair))
    {
        var frame = symbol.ToArray();
        if (decoder.TryDecodeFrame(frame.AsSpan(), decoded.AsSpan(), out var decodedLength))
        {
            recoveredPackets.AddRange(ParsePacketRecords(decoded.AsMemory(0, decodedLength)));
        }
    }

    AssertPacketSequence([packets[0], packets[2], packets[3]], directPackets);
    AssertPacketSequence([packets[1]], recoveredPackets);
    Assert(decoder.FecRecoveredPacketCount == 1,
        $"Expected one packet recovered by FEC, got {decoder.FecRecoveredPacketCount}.");

    return Task.CompletedTask;
}

static Task DecodedPacketKindsTrackEachOutputRecord()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 4,
        RepairSymbolsPerBlock = 2
    };
    var packets = new[]
    {
        DeterministicBytes(50),
        DeterministicBytes(52),
        DeterministicBytes(54),
        DeterministicBytes(56)
    };
    var symbols = EncodeRecordListToFrames(CreatePacketRecords(packets), options)
        .Select(frame => LinkerFecEncodedSymbol.Parse(frame, options))
        .ToArray();
    using var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];
    var packetKinds = new LinkerFecDecodedPacketKind[options.SourceSymbolsPerBlock];

    foreach (var repair in symbols.Where(symbol => symbol.IsRepair))
    {
        var frame = repair.ToArray();
        Assert(
            !decoder.TryDecodeFrame(
                frame.AsSpan(),
                decoded.AsSpan(),
                packetKinds.AsSpan(),
                out var decodedLength,
                out var decodedPacketCount),
            "Repair frames alone should not decode this block.");
        Assert(decodedLength == 0, "Incomplete repair decode should not report decoded bytes.");
        Assert(decodedPacketCount == 0, "Incomplete repair decode should not report decoded packets.");
    }

    var source0 = symbols.First(symbol => !symbol.IsRepair && symbol.SymbolId == 0).ToArray();
    Assert(
        decoder.TryDecodeFrame(
            source0.AsSpan(),
            decoded.AsSpan(),
            packetKinds.AsSpan(),
            out var source0Length,
            out var source0PacketCount),
        "Received source frame should be emitted directly.");
    Assert(source0PacketCount == 1, $"Expected one source packet, got {source0PacketCount}.");
    Assert(packetKinds[0] == LinkerFecDecodedPacketKind.Source, "Direct source output should be marked Source.");
    AssertPacketSequence([packets[0]], ParsePacketRecords(decoded.AsMemory(0, source0Length)));
    Assert(decoder.FecRecoveredPacketCount == 0, "Direct source output should not increment FEC recovery count.");

    var source2 = symbols.First(symbol => !symbol.IsRepair && symbol.SymbolId == 2).ToArray();
    Assert(
        decoder.TryDecodeFrame(
            source2.AsSpan(),
            decoded.AsSpan(),
            packetKinds.AsSpan(),
            out var mixedLength,
            out var mixedPacketCount),
        "Source frame should trigger recovery from buffered repair frames.");
    Assert(mixedPacketCount == 3, $"Expected one source and two recovered packets, got {mixedPacketCount}.");
    Assert(packetKinds[0] == LinkerFecDecodedPacketKind.Source, "First mixed output should be the received source packet.");
    Assert(packetKinds[1] == LinkerFecDecodedPacketKind.Recovered, "Second mixed output should be recovered.");
    Assert(packetKinds[2] == LinkerFecDecodedPacketKind.Recovered, "Third mixed output should be recovered.");
    AssertPacketSequence([packets[2], packets[1], packets[3]], ParsePacketRecords(decoded.AsMemory(0, mixedLength)));
    Assert(decoder.FecRecoveredPacketCount == 2,
        $"Expected two packets recovered by FEC, got {decoder.FecRecoveredPacketCount}.");

    return Task.CompletedTask;
}

static Task RecordLengthPrefixDoesNotConsumeSymbolCapacity()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 64,
        SourceSymbolsPerBlock = 2,
        RepairSymbolsPerBlock = 1
    };
    var packets = new[]
    {
        DeterministicBytes(options.SymbolSize),
        DeterministicBytes(options.SymbolSize)
    };
    var records = CreatePacketRecords(packets);
    var encoded = new byte[GetEncodedPacketSize(records.Length, options)];

    using var encoder = new LinkerFecCodec(options);
    var bytesWritten = encoder.EncodePacket(records.AsSpan(), encoded.AsSpan(), out var packetCount);
    Assert(packetCount == 3, $"Expected 2 source frames + 1 repair frame, got {packetCount} frames.");

    var frames = new List<byte[]>();
    AddLengthPrefixedFrames(encoded.AsSpan(0, bytesWritten), frames);
    Assert(frames.Count == 3, $"Expected 3 FEC frames, got {frames.Count}.");
    Assert(frames[0].Length == LinkerFecEncodedSymbol.HeaderSize + options.SymbolSize,
        "First source frame should contain only the payload bytes after the FEC header.");
    Assert(frames[1].Length == LinkerFecEncodedSymbol.HeaderSize + options.SymbolSize,
        "Second source frame should contain only the payload bytes after the FEC header.");
    Assert(frames[2].Length == LinkerFecEncodedSymbol.HeaderSize + sizeof(ushort) + options.SymbolSize,
        "Repair frame should contain length metadata plus the encoded payload bytes.");

    var decodedPackets = DecodePacketFrames(frames, options);
    AssertPacketSequence(packets, decodedPackets);
    return Task.CompletedTask;
}

static Task RepairProfileChoosesAbsoluteRepairSymbols()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 10,
        RepairSymbolsPerBlock = 4
    };

    Assert(options.GetRepairSymbolsForSourceCount(1) == 4, "Expected 10/4 to use four repairs for one source by default.");
    Assert(options.GetRepairSymbolsForSourceCount(3) == 4, "Expected 10/4 to use four repairs for three sources.");
    Assert(options.GetRepairSymbolsForSourceCount(10) == 4, "Expected 10/4 to use four repairs for a full source block.");

    var singleRecord = CreatePacketRecord(DeterministicBytes(17));
    var singleFrames = EncodeRecordListToFrames(singleRecord, options);
    Assert(singleFrames.Count == 5, $"Expected 1 source + 4 repair, got {singleFrames.Count} frames.");
    Assert(singleFrames.Count(frame => LinkerFecEncodedSymbol.Parse(frame, options).IsRepair) == 4,
        "Expected exactly four repair frames for one source.");

    var threeRecords = CreatePacketRecords([
        DeterministicBytes(17),
        DeterministicBytes(19),
        DeterministicBytes(23)
    ]);
    var threeFrames = EncodeRecordListToFrames(threeRecords, options);
    Assert(threeFrames.Count == 7, $"Expected 3 source + 4 repair, got {threeFrames.Count} frames.");
    Assert(threeFrames.Count(frame => LinkerFecEncodedSymbol.Parse(frame, options).IsRepair) == 4,
        "Expected exactly four repair frames for three sources.");

    return Task.CompletedTask;
}

static Task RepairProfileInterpolatesMissingSourceCounts()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 10,
        RepairSymbolsPerBlock = 1,
        RepairProfile = [
            new LinkerFecRepairProfilePoint(1, 2),
            new LinkerFecRepairProfilePoint(10, 4)
        ]
    };

    Assert(options.GetRepairSymbolsForSourceCount(1) == 2, "Expected profile 1:2 to use two repairs for one source.");
    Assert(options.GetRepairSymbolsForSourceCount(5) == 3, "Expected profile interpolation to use three repairs for five sources.");
    Assert(options.GetRepairSymbolsForSourceCount(10) == 4, "Expected profile 10:4 to use four repairs for ten sources.");

    var records = CreatePacketRecords(Enumerable.Range(0, 5).Select(i => DeterministicBytes(17 + i)).ToArray());
    var frames = EncodeRecordListToFrames(records, options);
    Assert(frames.Count == 8, $"Expected 5 source + 3 repair, got {frames.Count} frames.");
    Assert(frames.Count(frame => LinkerFecEncodedSymbol.Parse(frame, options).IsRepair) == 3,
        "Expected exactly three repair frames for five sources.");
    return Task.CompletedTask;
}

static Task SingleSourceProfileEmitsMultipleRepairSymbols()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 1,
        RepairSymbolsPerBlock = 3
    };

    Assert(options.GetRepairSymbolsForSourceCount(1) == 3, "Expected 1/3 to use three repairs for one source.");

    var packet = DeterministicBytes(17);
    var frames = EncodeRecordListToFrames(CreatePacketRecord(packet), options);
    Assert(frames.Count == 4, $"Expected 1 source + 3 repair, got {frames.Count} frames.");
    Assert(frames.Count(frame => LinkerFecEncodedSymbol.Parse(frame, options).IsRepair) == 3,
        "Expected exactly three repair frames for one source.");

    var repairsOnly = frames
        .Where(frame => LinkerFecEncodedSymbol.Parse(frame, options).IsRepair)
        .Take(1)
        .ToArray();
    var decoded = DecodeFrames(repairsOnly, options);
    AssertEqual(packet, decoded);

    return Task.CompletedTask;
}

static Task SingleSourceRepairFrameIsTrimmed()
{
    var options = new LinkerFecOptions
    {
        SourceSymbolsPerBlock = 1,
        RepairSymbolsPerBlock = 2
    };
    var raw = DeterministicBytes(123);
    var record = CreatePacketRecord(raw);
    var encoded = new byte[GetEncodedPacketSize(record.Length, options)];

    using var encoder = new LinkerFecCodec(options);
    var bytesWritten = encoder.EncodePacket(record.AsSpan(), encoded.AsSpan(), out var packetCount);
    Assert(packetCount == 3, $"Expected 3 encoded frames, got {packetCount}.");

    var sourceFrameLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(0, LinkerFecOptions.FrameLengthPrefixSize));
    Assert(sourceFrameLength == LinkerFecEncodedSymbol.HeaderSize + raw.Length, $"Expected trimmed source frame length, got {sourceFrameLength}.");

    var firstRepairLengthOffset = LinkerFecOptions.FrameLengthPrefixSize + sourceFrameLength;
    var firstRepairFrameLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(firstRepairLengthOffset, LinkerFecOptions.FrameLengthPrefixSize));
    Assert(firstRepairFrameLength == LinkerFecEncodedSymbol.HeaderSize + sizeof(ushort) + raw.Length, $"Expected trimmed first repair frame length, got {firstRepairFrameLength}.");

    var firstRepairFrameOffset = firstRepairLengthOffset + LinkerFecOptions.FrameLengthPrefixSize;
    var secondRepairLengthOffset = firstRepairFrameOffset + firstRepairFrameLength;
    var secondRepairFrameLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(secondRepairLengthOffset, LinkerFecOptions.FrameLengthPrefixSize));
    Assert(secondRepairFrameLength == LinkerFecEncodedSymbol.HeaderSize + sizeof(ushort) + raw.Length, $"Expected trimmed second repair frame length, got {secondRepairFrameLength}.");
    Assert(bytesWritten == (3 * LinkerFecOptions.FrameLengthPrefixSize) + sourceFrameLength + firstRepairFrameLength + secondRepairFrameLength, "Encoded byte count is inconsistent.");

    var repairSymbol = LinkerFecEncodedSymbol.Parse(encoded.AsSpan(firstRepairFrameOffset, firstRepairFrameLength), options);
    Assert(repairSymbol.IsRepair, "Second frame must be a repair symbol.");
    Assert(repairSymbol.Payload.Length == raw.Length, "Single-source repair payload should be trimmed to the payload length.");
    Assert(
        LinkerFecEncodedSymbol.TryGetFrameLength(encoded.AsSpan(firstRepairFrameOffset, firstRepairFrameLength), out var parsedRepairFrameLength),
        "Exact FEC frame should report its frame length.");
    Assert(parsedRepairFrameLength == firstRepairFrameLength, "Parsed frame length did not match the packetized frame length.");

    using var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];
    var decodedLength = decoder.DecodeFrame(encoded.AsSpan(firstRepairFrameOffset, firstRepairFrameLength), decoded.AsSpan());
    Assert(decodedLength == record.Length, $"Expected repair-only decode length {record.Length}, got {decodedLength}.");
    Assert(record.AsSpan().SequenceEqual(decoded.AsSpan(0, decodedLength)), "Repair-only decode differs from input.");
    Assert(decoder.FecRecoveredPacketCount == 1, "Single-source repair-only decode should count one FEC recovered packet.");
    AssertPacketSequence([raw], ParsePacketRecords(decoded.AsMemory(0, decodedLength)));

    return Task.CompletedTask;
}

static Task IntermediateRepairGenerationMatchesDirectRepair()
{
    var directOptions = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 16,
        RepairSymbolsPerBlock = 4,
        RepairGenerationMode = LinkerFecRepairGenerationMode.SourceCoefficients
    };

    var intermediateOptions = new LinkerFecOptions
    {
        SymbolSize = directOptions.SymbolSize,
        SourceSymbolsPerBlock = directOptions.SourceSymbolsPerBlock,
        RepairSymbolsPerBlock = directOptions.RepairSymbolsPerBlock,
        RepairGenerationMode = LinkerFecRepairGenerationMode.IntermediateSymbols
    };

    var packets = Enumerable.Range(0, directOptions.SourceSymbolsPerBlock)
        .Select(i => DeterministicBytes(60 + (i % 5)))
        .ToArray();
    var records = CreatePacketRecords(packets);
    var direct = EncodeRecordListBytes(records, directOptions);
    var intermediate = EncodeRecordListBytes(records, intermediateOptions);
    Assert(direct.AsSpan().SequenceEqual(intermediate), "Intermediate repair generation produced different FEC frames.");

    var frames = new List<byte[]>();
    AddLengthPrefixedFrames(intermediate, frames);
    var transmitted = DropSourceSymbols(frames, intermediateOptions, 1, 5, 9, 15);
    var decoded = DecodePacketFrames(transmitted, intermediateOptions);
    AssertPacketSet(packets, decoded);

    return Task.CompletedTask;
}

static Task PacketizedFrameLengthKeeps1400ByteFramesBounded()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 1400,
        SourceSymbolsPerBlock = 2,
        RepairSymbolsPerBlock = 1
    };
    var raw = DeterministicBytes(1396);
    var record = CreatePacketRecord(raw);
    var encoded = new byte[GetEncodedPacketSize(record.Length, options)];

    using var encoder = new LinkerFecCodec(options);
    var bytesWritten = encoder.EncodePacket(record.AsSpan(), encoded.AsSpan(), out var packetCount);
    Assert(packetCount == 2, $"Expected 2 encoded frames, got {packetCount}.");

    var offset = 0;
    while (offset < bytesWritten)
    {
        var frameLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(offset, LinkerFecOptions.FrameLengthPrefixSize));
        var frame = encoded.AsSpan(offset + LinkerFecOptions.FrameLengthPrefixSize, frameLength);
        var symbol = LinkerFecEncodedSymbol.Parse(frame, options);
        var expectedFrameLength = LinkerFecEncodedSymbol.HeaderSize +
            (symbol.IsRepair ? sizeof(ushort) : 0) +
            raw.Length;
        Assert(frameLength == expectedFrameLength, $"Expected a {expectedFrameLength}-byte frame, got {frameLength}.");
        Assert(
            LinkerFecEncodedSymbol.TryGetFrameLength(frame, out var parsedFrameLength),
            "Exact FEC frame should report its frame length.");
        Assert(parsedFrameLength == frameLength, "Parsed frame length did not match the packetized frame length.");
        offset += LinkerFecOptions.FrameLengthPrefixSize + frameLength;
    }

    Assert(offset == bytesWritten, "Encoded frame parser ended at an unexpected offset.");
    return Task.CompletedTask;
}

static Task CompactBlockIdWrapsAcrossUIntBoundary()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 1400,
        SourceSymbolsPerBlock = 1,
        RepairSymbolsPerBlock = 1,
        MaxSkipBlocks = 4
    };
    var decoder = new LinkerFecCodec(options);
    var dst = new byte[options.MaxDecodeBufferSize];
    var startBlockId = (ulong)uint.MaxValue - 1;

    try
    {
        for (var i = 0; i < 4; i++)
        {
            var raw = DeterministicBytes(16 + i);
            var record = CreatePacketRecord(raw);
            var symbol = new LinkerFecEncodedSymbol(
                startBlockId + (ulong)i,
                record.Length,
                options.SymbolSize,
                sourceSymbolCount: 1,
                options.RepairSymbolsPerBlock,
                symbolId: 0,
                isFinalBlock: false,
                raw);

            var frame = symbol.ToArray();
            var length = decoder.DecodeFrame(frame.AsSpan(), dst.AsSpan());
            Assert(length == record.Length, $"Decoded length mismatch across block id wrap at packet {i}.");
            Assert(record.AsSpan().SequenceEqual(dst.AsSpan(0, length)), $"Decoded payload mismatch across block id wrap at packet {i}.");
            AssertPacketSequence([raw], ParsePacketRecords(dst.AsMemory(0, length)));
        }
    }
    finally
    {
        decoder.Dispose();
    }

    return Task.CompletedTask;
}

static Task MaxSkipBlocksSkipsUnrecoverableGap()
{
    var options = PacketSkipTestOptions(maxSkipBlocks: 4);
    var packets = DeterministicPackets(10);
    var frames = EncodePacketsToFrames(packets, options);
    var transmitted = frames
        .Where(frame => frame.BlockId != 3)
        .Select(frame => frame.Frame)
        .ToList();

    var decoded = DecodePacketFrames(transmitted, options);
    var expected = packets
        .Where((_, index) => index != 3)
        .ToArray();

    AssertPacketSequence(expected, decoded);
    return Task.CompletedTask;
}

static Task MaxSkipBlocksKeepsRecentLatePackets()
{
    var options = PacketSkipTestOptions(maxSkipBlocks: 4);
    var packets = DeterministicPackets(10);
    var frames = EncodePacketsToFrames(packets, options);
    var transmitted = frames
        .Where(frame => frame.BlockId == 9)
        .Concat(frames.Where(frame => frame.BlockId == 4))
        .Concat(frames.Where(frame => frame.BlockId == 5))
        .Select(frame => frame.Frame)
        .ToList();

    var decoded = DecodePacketFrames(transmitted, options);
    AssertPacketSequence([packets[9], packets[5]], decoded);
    return Task.CompletedTask;
}

static Task CorruptFrameIsRejected()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 256,
        SourceSymbolsPerBlock = 16,
        RepairSymbolsPerBlock = 6
    };
    var raw = DeterministicBytes(1024);
    var frames = EncodeToFrames(raw, options);
    var corrupt = frames[0].ToArray();
    corrupt[0] ^= 0x55;

    if (LinkerFecEncodedSymbol.TryParse(corrupt, options, out _, out var error))
    {
        throw new InvalidOperationException("Corrupt frame was accepted.");
    }

    Assert(error.Contains("magic", StringComparison.OrdinalIgnoreCase), $"Unexpected error: {error}");
    return Task.CompletedTask;
}

static Task TooMuchLossDoesNotEmitRecoveredData()
{
    var options = TestOptions();
    var packets = Enumerable.Range(0, options.SourceSymbolsPerBlock)
        .Select(i => DeterministicBytes(20 + (i % 7)))
        .ToArray();
    var frames = EncodeRecordListToFrames(CreatePacketRecords(packets), options);

    // Drop more source symbols than the configured repair count can replace.
    var transmitted = DropSourceSymbols(frames, options, 0, 1, 2, 3, 4, 5, 6);
    var decoded = DecodePacketFrames(transmitted, options);
    AssertPacketSequence(packets[7..], decoded);
    return Task.CompletedTask;
}

static Task EmptyInputRoundTrip()
{
    var options = TestOptions();
    var frames = EncodeToFrames([], options);
    Assert(frames.Count == 1 + options.GetRepairSymbolsForSourceCount(1), "Empty input must still emit a final block.");
    var decoded = DecodeFrames(frames, options);
    AssertEqual([], decoded);
    return Task.CompletedTask;
}

static Task InvalidApplicationRecordsAreRejected()
{
    var options = TestOptions();
    var destination = new byte[GetEncodedPacketSize(LinkerFecOptions.RecordLengthPrefixSize, options)];
    var shortRecord = new byte[] { 1 };
    var incompleteRecord = new byte[] { 5, 0, 1 };
    using var encoder = new LinkerFecCodec(options);

    _ = Throws<ArgumentException>(() => encoder.EncodePacket(shortRecord, destination, out _));
    _ = Throws<ArgumentException>(() => encoder.TryEncodePacket(incompleteRecord, destination, out _, out _));

    return Task.CompletedTask;
}

static Task InvalidOptionsAreRejected()
{
    _ = Throws<ArgumentOutOfRangeException>(() => new LinkerFecCodec(new LinkerFecOptions { SymbolSize = 8 }));
    _ = Throws<ArgumentOutOfRangeException>(() => new LinkerFecCodec(new LinkerFecOptions { SourceSymbolsPerBlock = LinkerFecOptions.MaxSourceSymbolsPerBlock + 1 }));
    _ = Throws<ArgumentOutOfRangeException>(() => new LinkerFecCodec(new LinkerFecOptions { SourceSymbolsPerBlock = LinkerFecOptions.MaxSourceSymbolsPerBlock, RepairSymbolsPerBlock = LinkerFecOptions.MaxRepairSymbolsPerBlock }));
    _ = Throws<ArgumentOutOfRangeException>(() => new LinkerFecCodec(new LinkerFecOptions { MaxSkipBlocks = 0 }));
    _ = Throws<ArgumentOutOfRangeException>(() => new LinkerFecCodec(new LinkerFecOptions { MaxSkipBlocks = -1 }));
    _ = Throws<ArgumentOutOfRangeException>(() => new LinkerFecCodec(new LinkerFecOptions { MaxSkipBlocks = 8, MaxDecoderBlocks = 4 }));
    _ = Throws<ArgumentOutOfRangeException>(() => new LinkerFecCodec(new LinkerFecOptions { RepairGenerationMode = (LinkerFecRepairGenerationMode)42 }));
    _ = Throws<ArgumentException>(() => new LinkerFecCodec(new LinkerFecOptions { SourceSymbolsPerBlock = 10, RepairProfile = [] }));
    _ = Throws<ArgumentException>(() => new LinkerFecCodec(new LinkerFecOptions { SourceSymbolsPerBlock = 10, RepairProfile = [new LinkerFecRepairProfilePoint(1, 1)] }));
    _ = Throws<ArgumentException>(() => new LinkerFecCodec(new LinkerFecOptions { SourceSymbolsPerBlock = 10, RepairProfile = [new LinkerFecRepairProfilePoint(5, 1), new LinkerFecRepairProfilePoint(3, 1), new LinkerFecRepairProfilePoint(10, 2)] }));
    return Task.CompletedTask;
}

static LinkerFecOptions TestOptions()
{
    return new LinkerFecOptions
    {
        SymbolSize = 256,
        SourceSymbolsPerBlock = 16,
        RepairSymbolsPerBlock = 6
    };
}

static LinkerFecOptions PacketSkipTestOptions(int maxSkipBlocks)
{
    return new LinkerFecOptions
    {
        SymbolSize = 256,
        SourceSymbolsPerBlock = 2,
        RepairSymbolsPerBlock = 1,
        MaxSkipBlocks = maxSkipBlocks
    };
}

static List<byte[]> EncodeToFrames(byte[] raw, LinkerFecOptions options, int maxPacketLength = 1500)
{
    var frames = new List<byte[]>();
    var encoder = new LinkerFecCodec(options);
    try
    {
        var packetLimit = Math.Min(maxPacketLength, options.SymbolSize);
        var packetBuffer = new byte[GetEncodedPacketSize(packetLimit + LinkerFecOptions.RecordLengthPrefixSize, options)];

        if (raw.Length == 0)
        {
            Span<byte> emptyRecord = stackalloc byte[LinkerFecOptions.RecordLengthPrefixSize];
            var bytesWritten = encoder.EncodePacket(emptyRecord, packetBuffer, out _, isFinalPacket: true);
            AddLengthPrefixedFrames(packetBuffer.AsSpan(0, bytesWritten), frames);
            return frames;
        }

        var offset = 0;
        while (offset < raw.Length)
        {
            var packetLength = Math.Min(packetLimit, raw.Length - offset);
            var isFinal = offset + packetLength == raw.Length;
            var record = CreatePacketRecord(raw.AsSpan(offset, packetLength));
            if (!encoder.TryEncodePacket(record, packetBuffer, out var bytesWritten, out _, isFinal))
            {
                throw new InvalidOperationException("Packet encode buffer is too small.");
            }

            AddLengthPrefixedFrames(packetBuffer.AsSpan(0, bytesWritten), frames);
            offset += packetLength;
        }
    }
    finally
    {
        encoder.Dispose();
    }

    return frames;
}

static List<byte[]> EncodeRecordListToFrames(byte[] records, LinkerFecOptions options)
{
    var frames = new List<byte[]>();
    AddLengthPrefixedFrames(EncodeRecordListBytes(records, options), frames);
    return frames;
}

static byte[] EncodeRecordListBytes(byte[] records, LinkerFecOptions options)
{
    using var encoder = new LinkerFecCodec(options);
    var packetBuffer = new byte[GetEncodedPacketSize(records.Length, options)];
    var bytesWritten = encoder.EncodePacket(records.AsSpan(), packetBuffer.AsSpan(), out _, isFinalPacket: true);
    return packetBuffer.AsSpan(0, bytesWritten).ToArray();
}

static List<(ulong BlockId, byte[] Frame)> EncodePacketsToFrames(IReadOnlyList<byte[]> packets, LinkerFecOptions options)
{
    var frames = new List<(ulong BlockId, byte[] Frame)>();
    var encoder = new LinkerFecCodec(options);
    try
    {
        var maxPacketLength = packets.Count == 0 ? 0 : packets.Max(packet => packet.Length);
        var packetBuffer = new byte[GetEncodedPacketSize(maxPacketLength + LinkerFecOptions.RecordLengthPrefixSize, options)];

        for (var i = 0; i < packets.Count; i++)
        {
            var packet = packets[i];
            var record = CreatePacketRecord(packet);
            var bytesWritten = encoder.EncodePacket(
                record.AsSpan(),
                packetBuffer.AsSpan(),
                out _,
                isFinalPacket: i == packets.Count - 1);

            AddLengthPrefixedFramesWithBlockIds(packetBuffer.AsSpan(0, bytesWritten), options, frames);
        }
    }
    finally
    {
        encoder.Dispose();
    }

    return frames;
}

static void AddLengthPrefixedFrames(ReadOnlySpan<byte> packet, List<byte[]> frames)
{
    var offset = 0;
    while (offset < packet.Length)
    {
        var frameLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(offset, LinkerFecOptions.FrameLengthPrefixSize));
        offset += LinkerFecOptions.FrameLengthPrefixSize;
        frames.Add(packet.Slice(offset, frameLength).ToArray());
        offset += frameLength;
    }
}

static void AddLengthPrefixedFramesWithBlockIds(
    ReadOnlySpan<byte> packet,
    LinkerFecOptions options,
    List<(ulong BlockId, byte[] Frame)> frames)
{
    var offset = 0;
    while (offset < packet.Length)
    {
        var frameLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(offset, LinkerFecOptions.FrameLengthPrefixSize));
        offset += LinkerFecOptions.FrameLengthPrefixSize;

        var frame = packet.Slice(offset, frameLength).ToArray();
        var blockId = LinkerFecEncodedSymbol.Parse(frame, options).BlockId;
        frames.Add((blockId, frame));
        offset += frameLength;
    }
}

static byte[] DecodeFrames(IEnumerable<byte[]> frames, LinkerFecOptions options)
{
    var dst = new byte[options.MaxDecodeBufferSize];
    using var output = new MemoryStream();
    var decoder = new LinkerFecCodec(options);
    try
    {
        foreach (var frame in frames)
        {
            var length = decoder.DecodeFrame(frame.AsSpan(), dst.AsSpan());
            if (length > 0)
            {
                WritePacketRecordsPayloads(dst.AsSpan(0, length), output);
            }
        }
    }
    finally
    {
        decoder.Dispose();
    }

    return output.ToArray();
}

static List<byte[]> DecodePacketFrames(IEnumerable<byte[]> frames, LinkerFecOptions options)
{
    var decoded = new List<byte[]>();
    var dst = new byte[options.MaxDecodeBufferSize];
    var decoder = new LinkerFecCodec(options);
    try
    {
        foreach (var frame in frames)
        {
            if (decoder.TryDecodeFrame(frame.AsSpan(), dst.AsSpan(), out var length))
            {
                decoded.AddRange(ParsePacketRecords(dst.AsMemory(0, length)));
            }
        }
    }
    finally
    {
        decoder.Dispose();
    }

    return decoded;
}

static List<byte[]> DropSourceSymbols(IEnumerable<byte[]> frames, LinkerFecOptions options, params int[] sourceSymbolIds)
{
    var drop = sourceSymbolIds.ToHashSet();
    return frames
        .Select(frame => LinkerFecEncodedSymbol.Parse(frame, options))
        .Where(symbol => symbol.IsRepair || !drop.Contains(symbol.SymbolId))
        .Select(symbol => symbol.ToArray())
        .ToList();
}

static int GetEncodedPacketSize(int rawPacketLength, LinkerFecOptions options)
{
    var sourceCount = Math.Min(options.SourceSymbolsPerBlock, Math.Max(1, rawPacketLength / LinkerFecOptions.RecordLengthPrefixSize));
    return checked(
        (sourceCount * (LinkerFecOptions.FrameLengthPrefixSize + LinkerFecEncodedSymbol.HeaderSize)) +
        rawPacketLength +
        (options.MaxRepairSymbolsPerEncodedBlock * (LinkerFecOptions.FrameLengthPrefixSize + LinkerFecEncodedSymbol.HeaderSize + sizeof(ushort) + options.SymbolSize)));
}

static int GetIntArg(string[] args, int index, int defaultValue)
{
    return args.Length > index && int.TryParse(args[index], out var value) ? value : defaultValue;
}

static int GetOptionInt(string[] args, string name, int defaultValue)
{
    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)
            && i + 1 < args.Length
            && int.TryParse(args[i + 1], out var value))
        {
            return value;
        }

        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(arg[(name.Length + 1)..], out value))
        {
            return value;
        }
    }

    return defaultValue;
}

static byte[] DeterministicBytes(int length)
{
    var data = new byte[length];
    var state = 0x1234_5678u;
    for (var i = 0; i < data.Length; i++)
    {
        state = (state * 1_664_525u) + 1_013_904_223u;
        data[i] = (byte)(state >> 24);
    }

    return data;
}

static byte[][] DeterministicPackets(int count)
{
    var packets = new byte[count][];
    for (var i = 0; i < packets.Length; i++)
    {
        packets[i] = DeterministicBytes(180 + i);
    }

    return packets;
}

static void AssertEqual(byte[] expected, byte[] actual)
{
    Assert(expected.AsSpan().SequenceEqual(actual), $"Expected {expected.Length} bytes, got {actual.Length} bytes.");
}

static void AssertPacketSequence(IReadOnlyList<byte[]> expected, IReadOnlyList<byte[]> actual)
{
    Assert(actual.Count == expected.Count, $"Expected {expected.Count} decoded packets, got {actual.Count}.");
    for (var i = 0; i < expected.Count; i++)
    {
        Assert(expected[i].AsSpan().SequenceEqual(actual[i]), $"Decoded packet {i} differs from expected packet.");
    }
}

static void AssertPacketSet(IReadOnlyList<byte[]> expected, IReadOnlyList<byte[]> actual)
{
    Assert(actual.Count == expected.Count, $"Expected {expected.Count} decoded packets, got {actual.Count}.");
    var matched = new bool[actual.Count];
    for (var expectedIndex = 0; expectedIndex < expected.Count; expectedIndex++)
    {
        var found = false;
        for (var actualIndex = 0; actualIndex < actual.Count; actualIndex++)
        {
            if (matched[actualIndex] || !expected[expectedIndex].AsSpan().SequenceEqual(actual[actualIndex]))
            {
                continue;
            }

            matched[actualIndex] = true;
            found = true;
            break;
        }

        Assert(found, $"Expected packet {expectedIndex} was not decoded.");
    }
}

static List<byte[]> ParsePacketRecords(ReadOnlyMemory<byte> packetBatch)
{
    var packets = new List<byte[]>();
    var span = packetBatch.Span;
    var offset = 0;

    while (offset < span.Length)
    {
        Assert(span.Length - offset >= LinkerFecOptions.RecordLengthPrefixSize, "Packet record list ended inside a length prefix.");
        var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, LinkerFecOptions.RecordLengthPrefixSize));
        offset += LinkerFecOptions.RecordLengthPrefixSize;
        Assert(packetLength <= span.Length - offset, "Packet record list ended inside a packet payload.");

        packets.Add(span.Slice(offset, packetLength).ToArray());
        offset += packetLength;
    }

    return packets;
}

static byte[] CreatePacketRecord(ReadOnlySpan<byte> packet)
{
    var record = new byte[LinkerFecOptions.RecordLengthPrefixSize + packet.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0, LinkerFecOptions.RecordLengthPrefixSize), checked((ushort)packet.Length));
    packet.CopyTo(record.AsSpan(LinkerFecOptions.RecordLengthPrefixSize));
    return record;
}

static byte[] CreatePacketRecords(IReadOnlyList<byte[]> packets)
{
    var length = packets.Sum(static packet => LinkerFecOptions.RecordLengthPrefixSize + packet.Length);
    var records = new byte[length];
    var offset = 0;
    foreach (var packet in packets)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(records.AsSpan(offset, LinkerFecOptions.RecordLengthPrefixSize), checked((ushort)packet.Length));
        offset += LinkerFecOptions.RecordLengthPrefixSize;
        packet.CopyTo(records.AsSpan(offset, packet.Length));
        offset += packet.Length;
    }

    return records;
}

static byte[] ConcatPackets(IReadOnlyList<byte[]> packets)
{
    var length = packets.Sum(static packet => packet.Length);
    var raw = new byte[length];
    var offset = 0;
    foreach (var packet in packets)
    {
        packet.CopyTo(raw.AsSpan(offset, packet.Length));
        offset += packet.Length;
    }

    return raw;
}

static void WritePacketRecordsPayloads(ReadOnlySpan<byte> records, Stream output)
{
    var offset = 0;
    while (offset < records.Length)
    {
        Assert(records.Length - offset >= LinkerFecOptions.RecordLengthPrefixSize, "Decoded packet record list ended inside a length prefix.");
        var packetLength = BinaryPrimitives.ReadUInt16LittleEndian(records.Slice(offset, LinkerFecOptions.RecordLengthPrefixSize));
        offset += LinkerFecOptions.RecordLengthPrefixSize;
        Assert(packetLength <= records.Length - offset, "Decoded packet record list ended inside a packet payload.");

        output.Write(records.Slice(offset, packetLength));
        offset += packetLength;
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static T Throws<T>(Action action)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T ex)
    {
        return ex;
    }

    throw new InvalidOperationException($"Expected exception {typeof(T).Name} was not thrown.");
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
