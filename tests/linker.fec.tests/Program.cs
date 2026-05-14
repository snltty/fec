using System.Buffers.Binary;
using System.Diagnostics;
using linker.fec;

if (args.Length > 0 && string.Equals(args[0], "--stress-random-roundtrip", StringComparison.OrdinalIgnoreCase))
{
    var iterations = args.Length > 1
        ? ParseIterationCount(args[1])
        : 100_000_000L;
    RunRandomRoundTripStress(iterations);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "--loss-sweep", StringComparison.OrdinalIgnoreCase))
{
    var packetCount = args.Length > 1 ? ParsePositiveInt32(args[1], "packet count") : 1400;
    var packetLength = args.Length > 2 ? ParsePositiveInt32(args[2], "packet length") : 1400;
    var trials = args.Length > 3 ? ParsePositiveInt32(args[3], "trial count") : 100;
    RunDefaultOptionsLossSweep(packetCount, packetLength, trials);
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
    ("partial source count scales repair symbols", PartialSourceCountScalesRepairSymbols),
    ("single source ratio emits multiple repair symbols", SingleSourceRatioEmitsMultipleRepairSymbols),
    ("single source repair frame is trimmed", SingleSourceRepairFrameIsTrimmed),
    ("intermediate repair generation matches direct repair", IntermediateRepairGenerationMatchesDirectRepair),
    ("payload-length header keeps 1400 byte frames self-delimiting", PayloadLengthHeaderKeeps1400ByteFramesSelfDelimiting),
    ("compact block id wraps across uint boundary", CompactBlockIdWrapsAcrossUIntBoundary),
    ("max skip blocks skips unrecoverable gap", MaxSkipBlocksSkipsUnrecoverableGap),
    ("max skip blocks keeps recent late packets", MaxSkipBlocksKeepsRecentLatePackets),
    ("serialized frames round trip", SerializedFramesRoundTrip),
    ("corrupt frame is rejected", CorruptFrameIsRejected),
    ("too much loss emits only received source data", TooMuchLossDoesNotEmitRecoveredData),
    ("batched packets round trip", PacketBatchRoundTrip),
    ("batched packets decode to length-prefixed list", PacketBatchDecodesToLengthPrefixedList),
    ("batched packets recover missing source symbol", PacketBatchRecoversMissingSourceSymbol),
    ("batched packets split at fec block size", PacketBatchSplitsAtFecBlockSize),
    ("batched packets split at source packet count", PacketBatchSplitsAtSourcePacketCount),
    ("batched packets large backlog round trip", PacketBatchLargeBacklogRoundTrip),
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
            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(offset, sizeof(int)));
            offset += sizeof(int);
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

    var decodedPackets = DecodeBatchedFrames(frames, options);
    AssertPacketSequence(packets, decodedPackets);
    return Task.CompletedTask;
}

static Task PartialSourceCountScalesRepairSymbols()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 10,
        RepairSymbolsPerBlock = 4
    };

    Assert(options.GetRepairSymbolsForSourceCount(1) == 1, "Expected 10/4 to use one repair for one source by default.");
    Assert(options.GetRepairSymbolsForSourceCount(3) == 2, "Expected 10/4 to use two repairs for three sources.");
    Assert(options.GetRepairSymbolsForSourceCount(10) == 4, "Expected 10/4 to use four repairs for a full source block.");

    var singleRecord = CreatePacketRecord(DeterministicBytes(17));
    var singleFrames = EncodeRecordListToFrames(singleRecord, options);
    Assert(singleFrames.Count == 2, $"Expected 1 source + 1 repair, got {singleFrames.Count} frames.");
    Assert(singleFrames.Count(frame => LinkerFecEncodedSymbol.Parse(frame, options).IsRepair) == 1,
        "Expected exactly one repair frame for one source.");

    var threeRecords = CreatePacketRecords([
        DeterministicBytes(17),
        DeterministicBytes(19),
        DeterministicBytes(23)
    ]);
    var threeFrames = EncodeRecordListToFrames(threeRecords, options);
    Assert(threeFrames.Count == 5, $"Expected 3 source + 2 repair, got {threeFrames.Count} frames.");
    Assert(threeFrames.Count(frame => LinkerFecEncodedSymbol.Parse(frame, options).IsRepair) == 2,
        "Expected exactly two repair frames for three sources.");

    return Task.CompletedTask;
}

static Task SingleSourceRatioEmitsMultipleRepairSymbols()
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

    var sourceFrameLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(0, sizeof(int)));
    Assert(sourceFrameLength == LinkerFecEncodedSymbol.HeaderSize + raw.Length, $"Expected trimmed source frame length, got {sourceFrameLength}.");

    var firstRepairLengthOffset = sizeof(int) + sourceFrameLength;
    var firstRepairFrameLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(firstRepairLengthOffset, sizeof(int)));
    Assert(firstRepairFrameLength == LinkerFecEncodedSymbol.HeaderSize + sizeof(ushort) + raw.Length, $"Expected trimmed first repair frame length, got {firstRepairFrameLength}.");

    var firstRepairFrameOffset = firstRepairLengthOffset + sizeof(int);
    var secondRepairLengthOffset = firstRepairFrameOffset + firstRepairFrameLength;
    var secondRepairFrameLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(secondRepairLengthOffset, sizeof(int)));
    Assert(secondRepairFrameLength == LinkerFecEncodedSymbol.HeaderSize + sizeof(ushort) + raw.Length, $"Expected trimmed second repair frame length, got {secondRepairFrameLength}.");
    Assert(bytesWritten == (3 * sizeof(int)) + sourceFrameLength + firstRepairFrameLength + secondRepairFrameLength, "Encoded byte count is inconsistent.");

    var repairSymbol = LinkerFecEncodedSymbol.Parse(encoded.AsSpan(firstRepairFrameOffset, firstRepairFrameLength), options);
    Assert(repairSymbol.IsRepair, "Second frame must be a repair symbol.");
    Assert(repairSymbol.Payload.Length == raw.Length, "Single-source repair payload should be trimmed to the payload length.");
    Assert(
        LinkerFecEncodedSymbol.TryGetFrameLength(encoded.AsSpan(firstRepairFrameOffset, bytesWritten - firstRepairFrameOffset), out var parsedRepairFrameLength),
        "Payload length in the FEC header should make the first repair frame self-delimiting.");
    Assert(parsedRepairFrameLength == firstRepairFrameLength, "Header payload length did not identify the first repair frame length.");

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

static Task PayloadLengthHeaderKeeps1400ByteFramesSelfDelimiting()
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
        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(offset, sizeof(int)));
        var frame = encoded.AsSpan(offset + sizeof(int), frameLength);
        var symbol = LinkerFecEncodedSymbol.Parse(frame, options);
        var expectedFrameLength = LinkerFecEncodedSymbol.HeaderSize +
            (symbol.IsRepair ? sizeof(ushort) : 0) +
            raw.Length;
        Assert(frameLength == expectedFrameLength, $"Expected a {expectedFrameLength}-byte frame, got {frameLength}.");
        Assert(
            LinkerFecEncodedSymbol.TryGetFrameLength(frame, out var parsedFrameLength),
            "Payload length in the FEC header should make frames self-delimiting.");
        Assert(parsedFrameLength == frameLength, "Header payload length did not match the packetized frame length.");
        offset += sizeof(int) + frameLength;
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

static async Task PacketBatchRoundTrip()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 4,
        RepairSymbolsPerBlock = 2
    };

    var packets = new[]
    {
        DeterministicBytes(17),
        DeterministicBytes(31),
        DeterministicBytes(43)
    };

    using var encoder = new LinkerFecPacketBatcher(maxRemaining: 16 * 1024, options);
    using var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];

    foreach (var packet in packets)
    {
        await encoder.WriteAsync(packet);
    }

    var encoded = await encoder.ReadAsync();
    Assert(encoded.Length > 0, "Packet batcher did not emit encoded data.");
    Assert(encoder.LastRawBytes == packets.Sum(packet => packet.Length + sizeof(int)), "Packet batcher did not batch the queued packets.");

    var packetBatch = DecodeBatchedEncodedPacket(encoded, decoder, decoded);
    Assert(!packetBatch.IsEmpty, "Batch decode did not emit a packet list.");
    var decodedPackets = ParsePacketRecords(packetBatch);
    AssertPacketSequence(packets, decodedPackets);
}

static async Task PacketBatchDecodesToLengthPrefixedList()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 128,
        SourceSymbolsPerBlock = 10,
        RepairSymbolsPerBlock = 2
    };

    var packets = new[]
    {
        DeterministicBytes(17),
        DeterministicBytes(31),
        DeterministicBytes(43),
        DeterministicBytes(59),
        DeterministicBytes(61),
        DeterministicBytes(63)
    };

    using var encoder = new LinkerFecPacketBatcher(maxRemaining: 16 * 1024, options);
    using var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];

    foreach (var packet in packets)
    {
        await encoder.WriteAsync(packet);
    }

    var encoded = await encoder.ReadAsync();
    var expectedSourceFrames = packets.Length;
    var expectedFrameCount = expectedSourceFrames + options.RepairSymbolsPerBlock;
    Assert(encoder.LastEncodedFrameCount == expectedFrameCount,
        $"Expected {expectedFrameCount} FEC frames for the packet batch, got {encoder.LastEncodedFrameCount}.");

    var packetBatch = DecodeBatchedEncodedPacket(encoded, decoder, decoded);
    Assert(packetBatch.Length == packets.Sum(packet => packet.Length + sizeof(int)),
        "Batch decode should emit one complete length-prefixed packet list.");
    AssertPacketSequence(packets, ParsePacketRecords(packetBatch));
}

static async Task PacketBatchRecoversMissingSourceSymbol()
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

    using var encoder = new LinkerFecPacketBatcher(maxRemaining: 16 * 1024, options);

    foreach (var packet in packets)
    {
        await encoder.WriteAsync(packet);
    }

    var encoded = await encoder.ReadAsync();
    var frames = new List<byte[]>();
    AddLengthPrefixedFrames(encoded.Span, frames);
    var transmitted = frames
        .Select(frame => LinkerFecEncodedSymbol.Parse(frame, options))
        .Where(symbol => symbol.IsRepair || symbol.SymbolId != 1)
        .Select(symbol => symbol.ToArray())
        .ToArray();

    var decodedPackets = DecodeBatchedFrames(transmitted, options);
    AssertPacketSet(packets, decodedPackets);
}

static async Task PacketBatchSplitsAtFecBlockSize()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 64,
        SourceSymbolsPerBlock = 2,
        RepairSymbolsPerBlock = 1
    };

    var packets = new[]
    {
        DeterministicBytes(50),
        DeterministicBytes(60),
        DeterministicBytes(20)
    };

    using var encoder = new LinkerFecPacketBatcher(maxRemaining: 16 * 1024, options);
    using var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];

    foreach (var packet in packets)
    {
        await encoder.WriteAsync(packet);
    }

    var firstEncoded = await encoder.ReadAsync();
    Assert(encoder.LastRawBytes == 118, $"Expected first batch to use 118 bytes, got {encoder.LastRawBytes}.");
    var firstBatchPackets = ParsePacketRecords(DecodeBatchedEncodedPacket(firstEncoded, decoder, decoded));
    AssertPacketSequence(packets[..2], firstBatchPackets);

    var secondEncoded = await encoder.ReadAsync();
    Assert(encoder.LastRawBytes == 24, $"Expected second batch to use 24 bytes, got {encoder.LastRawBytes}.");
    var secondBatchPackets = ParsePacketRecords(DecodeBatchedEncodedPacket(secondEncoded, decoder, decoded));
    AssertPacketSequence(packets[2..], secondBatchPackets);
}

static async Task PacketBatchSplitsAtSourcePacketCount()
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 1440,
        SourceSymbolsPerBlock = 10,
        RepairSymbolsPerBlock = 2
    };

    var packets = Enumerable.Range(0, 20)
        .Select(i => DeterministicBytes(64 + i % 2))
        .ToArray();

    using var encoder = new LinkerFecPacketBatcher(maxRemaining: 16 * 1024, options);
    using var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];

    foreach (var packet in packets)
    {
        await encoder.WriteAsync(packet);
    }

    var firstEncoded = await encoder.ReadAsync();
    Assert(encoder.LastRawPacketCount == options.SourceSymbolsPerBlock,
        $"Expected first batch to contain {options.SourceSymbolsPerBlock} packets, got {encoder.LastRawPacketCount}.");
    Assert(encoder.LastEncodedFrameCount == options.SourceSymbolsPerBlock + options.RepairSymbolsPerBlock,
        $"Expected batch 10/2 to emit 12 FEC frames, got {encoder.LastEncodedFrameCount}.");
    var firstBatchPackets = ParsePacketRecords(DecodeBatchedEncodedPacket(firstEncoded, decoder, decoded));
    AssertPacketSequence(packets[..options.SourceSymbolsPerBlock], firstBatchPackets);

    var secondEncoded = await encoder.ReadAsync();
    Assert(encoder.LastRawPacketCount == options.SourceSymbolsPerBlock,
        $"Expected second batch to contain {options.SourceSymbolsPerBlock} packets, got {encoder.LastRawPacketCount}.");
    Assert(encoder.LastEncodedFrameCount == options.SourceSymbolsPerBlock + options.RepairSymbolsPerBlock,
        $"Expected batch 10/2 to emit 12 FEC frames, got {encoder.LastEncodedFrameCount}.");
    var secondBatchPackets = ParsePacketRecords(DecodeBatchedEncodedPacket(secondEncoded, decoder, decoded));
    AssertPacketSequence(packets[options.SourceSymbolsPerBlock..], secondBatchPackets);
}

static async Task PacketBatchLargeBacklogRoundTrip()
{
    var options = new LinkerFecOptions
    {
        SourceSymbolsPerBlock = 10,
        RepairSymbolsPerBlock = 2
    };

    const int packetCount = 10_000;
    const int packetLength = 64;
    using var encoder = new LinkerFecPacketBatcher(maxRemaining: 64 * 1024 * 1024, options);
    using var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];

    for (var i = 0; i < packetCount; i++)
    {
        await encoder.WriteAsync(DeterministicBytes(packetLength + i % 3));
    }

    var decodedPackets = 0;
    while (decodedPackets < packetCount)
    {
        var encoded = await encoder.ReadAsync();
        Assert(!encoded.IsEmpty, "Packet batcher completed before all backlog packets were encoded.");

        var packetBatch = DecodeBatchedEncodedPacket(encoded, decoder, decoded);
        if (!packetBatch.IsEmpty)
        {
            var packets = ParsePacketRecords(packetBatch);
            decodedPackets += packets.Count;
        }
    }

    Assert(decodedPackets == packetCount, $"Expected {packetCount} decoded batched packets, got {decodedPackets}.");
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
    var destination = new byte[GetEncodedPacketSize(sizeof(int), options)];
    var shortRecord = new byte[] { 1, 2, 3 };
    var incompleteRecord = new byte[] { 5, 0, 0, 0, 1 };
    using var encoder = new LinkerFecCodec(options);

    _ = Throws<ArgumentException>(() => encoder.EncodePacket(shortRecord, destination, out _));
    _ = Throws<ArgumentException>(() => encoder.TryEncodePacket(incompleteRecord, destination, out _, out _));

    using var decoder = new LinkerFecCodec(options);
    var decoded = new byte[options.MaxDecodeBufferSize];
    var invalidRecordFrame = new LinkerFecEncodedSymbol(
        0,
        4,
        options.SymbolSize,
        1,
        options.RepairSymbolsPerBlock,
        0,
        false,
        new byte[] { 1, 2, 3, 4 }).ToArray();

    _ = Throws<InvalidDataException>(() => decoder.DecodeFrame(invalidRecordFrame.AsSpan(), decoded.AsSpan()));
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
        var packetBuffer = new byte[GetEncodedPacketSize(packetLimit + sizeof(int), options)];

        if (raw.Length == 0)
        {
            Span<byte> emptyRecord = stackalloc byte[sizeof(int)];
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
        var packetBuffer = new byte[GetEncodedPacketSize(maxPacketLength + sizeof(int), options)];

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
        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        frames.Add(packet.Slice(offset, frameLength).ToArray());
        offset += frameLength;
    }
}

static ReadOnlyMemory<byte> DecodeBatchedEncodedPacket(
    ReadOnlyMemory<byte> encodedPacket,
    LinkerFecCodec decoder,
    byte[] decoded)
{
    using var output = new MemoryStream();
    var span = encodedPacket.Span;
    var offset = 0;
    while (offset < span.Length)
    {
        Assert(span.Length - offset >= sizeof(int), "Batched encoded packet ended inside a frame length prefix.");
        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        Assert(frameLength > 0 && frameLength <= span.Length - offset,
            $"Invalid batched FEC frame length {frameLength} at offset {offset}.");

        if (decoder.TryDecodeFrame(span.Slice(offset, frameLength), decoded.AsSpan(), out var decodedLength))
        {
            output.Write(decoded.AsSpan(0, decodedLength));
        }

        offset += frameLength;
    }

    return output.ToArray();
}

static List<byte[]> DecodeBatchedFrames(IEnumerable<byte[]> frames, LinkerFecOptions options)
{
    var decodedPackets = new List<byte[]>();
    var decoded = new byte[options.MaxDecodeBufferSize];
    using var decoder = new LinkerFecCodec(options);

    foreach (var frame in frames)
    {
        if (decoder.TryDecodeFrame(frame.AsSpan(), decoded.AsSpan(), out var decodedLength))
        {
            decodedPackets.AddRange(ParsePacketRecords(decoded.AsMemory(0, decodedLength)));
        }
    }

    return decodedPackets;
}

static void AddLengthPrefixedFramesWithBlockIds(
    ReadOnlySpan<byte> packet,
    LinkerFecOptions options,
    List<(ulong BlockId, byte[] Frame)> frames)
{
    var offset = 0;
    while (offset < packet.Length)
    {
        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset, sizeof(int)));
        offset += sizeof(int);

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
    var sourceCount = Math.Min(options.SourceSymbolsPerBlock, Math.Max(1, rawPacketLength / sizeof(int)));
    return checked(
        (sourceCount * (sizeof(int) + LinkerFecEncodedSymbol.HeaderSize)) +
        rawPacketLength +
        (options.RepairSymbolsPerBlock * (sizeof(int) + LinkerFecEncodedSymbol.HeaderSize + sizeof(ushort) + options.SymbolSize)));
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
        Assert(span.Length - offset >= sizeof(int), "Packet record list ended inside a length prefix.");
        var packetLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        Assert(packetLength >= 0, "Batched packet length cannot be negative.");
        Assert(packetLength <= span.Length - offset, "Packet record list ended inside a packet payload.");

        packets.Add(span.Slice(offset, packetLength).ToArray());
        offset += packetLength;
    }

    return packets;
}

static byte[] CreatePacketRecord(ReadOnlySpan<byte> packet)
{
    var record = new byte[sizeof(int) + packet.Length];
    BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, sizeof(int)), packet.Length);
    packet.CopyTo(record.AsSpan(sizeof(int)));
    return record;
}

static byte[] CreatePacketRecords(IReadOnlyList<byte[]> packets)
{
    var length = packets.Sum(static packet => sizeof(int) + packet.Length);
    var records = new byte[length];
    var offset = 0;
    foreach (var packet in packets)
    {
        BinaryPrimitives.WriteInt32LittleEndian(records.AsSpan(offset, sizeof(int)), packet.Length);
        offset += sizeof(int);
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
        Assert(records.Length - offset >= sizeof(int), "Decoded packet record list ended inside a length prefix.");
        var packetLength = BinaryPrimitives.ReadInt32LittleEndian(records.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        Assert(packetLength >= 0, "Decoded packet length cannot be negative.");
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

static long ParseIterationCount(string value)
{
    if (long.TryParse(value, out var count) && count > 0)
    {
        return count;
    }

    throw new ArgumentOutOfRangeException(nameof(value), value, "Stress iteration count must be a positive integer.");
}

static int ParsePositiveInt32(string value, string name)
{
    if (int.TryParse(value, out var count) && count > 0)
    {
        return count;
    }

    throw new ArgumentOutOfRangeException(nameof(value), value, $"{name} must be a positive integer.");
}

static void RunDefaultOptionsLossSweep(int packetCount, int packetLength, int trials)
{
    var options = new LinkerFecOptions();
    if (packetLength > options.SymbolSize)
    {
        throw new ArgumentOutOfRangeException(
            nameof(packetLength),
            packetLength,
            $"The default single-packet sweep expects packet length <= SymbolSize ({options.SymbolSize}).");
    }

    var packets = CreateDeterministicPackets(packetCount, packetLength);
    var frames = EncodeLossSweepFrames(packets, options);
    var rawBytes = (long)packetCount * packetLength;
    var frameCount = frames.Count;
    var expectedFrameCount = packetCount * (1 + options.RepairSymbolsPerBlock);
    Assert(frameCount == expectedFrameCount, $"Expected {expectedFrameCount} FEC frames, got {frameCount}.");

    var random = new FastRandom(0x4C4F_5353_5357_4545UL);
    var order = new int[frameCount];
    var dropped = new bool[frameCount];
    var recoverablePackets = new bool[packetCount];
    var destination = new byte[options.MaxDecodeBufferSize];

    Console.WriteLine(
        $"Default LinkerFecOptions loss sweep: packets={packetCount}, packetLength={packetLength}, trials={trials}");
    Console.WriteLine(
        $"Options: SymbolSize={options.SymbolSize}, SourceSymbolsPerBlock={options.SourceSymbolsPerBlock}, " +
        $"RepairSymbolsPerBlock={options.RepairSymbolsPerBlock}");
    Console.WriteLine($"Encoded frames: {frameCount} ({frameCount / (double)packetCount:N2} frames/source packet)");
    Console.WriteLine();
    Console.WriteLine("Loss%  Dropped  AvgRecovered  MinRecovered  MaxRecovered  Theoretical");

    for (var lossPercent = 0; lossPercent <= 100; lossPercent += 5)
    {
        var dropCount = (frameCount * lossPercent + 50) / 100;
        var totalRecoveredBytes = 0L;
        var minRecoveredPercent = double.PositiveInfinity;
        var maxRecoveredPercent = 0d;

        for (var trial = 0; trial < trials; trial++)
        {
            var recoveredBytes = RunLossSweepTrial(
                packets,
                frames,
                options,
                ref random,
                order,
                dropped,
                recoverablePackets,
                destination,
                dropCount);

            var recoveredPercent = recoveredBytes * 100d / rawBytes;
            totalRecoveredBytes += recoveredBytes;
            minRecoveredPercent = Math.Min(minRecoveredPercent, recoveredPercent);
            maxRecoveredPercent = Math.Max(maxRecoveredPercent, recoveredPercent);
        }

        var averageRecoveredPercent = totalRecoveredBytes * 100d / (rawBytes * trials);
        var lossProbability = lossPercent / 100d;
        var theoreticalRecoveredPercent = (1d - (lossProbability * lossProbability)) * 100d;
        Console.WriteLine(
            $"{lossPercent,5}%  {dropCount,7}  {averageRecoveredPercent,11:N2}%  " +
            $"{minRecoveredPercent,11:N2}%  {maxRecoveredPercent,11:N2}%  {theoreticalRecoveredPercent,10:N2}%");
    }
}

static long RunLossSweepTrial(
    IReadOnlyList<byte[]> packets,
    IReadOnlyList<(int PacketIndex, byte[] Frame)> frames,
    LinkerFecOptions options,
    ref FastRandom random,
    int[] order,
    bool[] dropped,
    bool[] recoverablePackets,
    byte[] destination,
    int dropCount)
{
    Array.Clear(dropped);
    Array.Clear(recoverablePackets);
    for (var i = 0; i < order.Length; i++)
    {
        order[i] = i;
    }

    for (var i = 0; i < dropCount; i++)
    {
        var swapIndex = i + random.NextInt32(0, order.Length - i);
        (order[i], order[swapIndex]) = (order[swapIndex], order[i]);
        dropped[order[i]] = true;
    }

    for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
    {
        if (!dropped[frameIndex])
        {
            recoverablePackets[frames[frameIndex].PacketIndex] = true;
        }
    }

    var recoveredPackets = 0;
    var recoveredBytes = 0L;
    var nextExpectedPacket = 0;
    using var decoder = new LinkerFecCodec(options);

    for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
    {
        if (dropped[frameIndex])
        {
            continue;
        }

        if (!decoder.TryDecodeFrame(frames[frameIndex].Frame.AsSpan(), destination.AsSpan(), out var decodedLength))
        {
            continue;
        }

        while (nextExpectedPacket < recoverablePackets.Length && !recoverablePackets[nextExpectedPacket])
        {
            nextExpectedPacket++;
        }

        if (nextExpectedPacket >= recoverablePackets.Length)
        {
            throw new InvalidOperationException("Decoder emitted more packets than expected.");
        }

        var expected = packets[nextExpectedPacket];
        var decodedPackets = ParsePacketRecords(destination.AsMemory(0, decodedLength));
        if (decodedPackets.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected one decoded packet record for packet {nextExpectedPacket}, got {decodedPackets.Count}.");
        }

        var decodedPacket = decodedPackets[0];
        if (decodedPacket.Length != expected.Length)
        {
            throw new InvalidOperationException(
                $"Decoded length mismatch for packet {nextExpectedPacket}: expected {expected.Length}, got {decodedPacket.Length}.");
        }

        if (!expected.AsSpan().SequenceEqual(decodedPacket))
        {
            throw new InvalidOperationException($"Decoded payload mismatch for packet {nextExpectedPacket}.");
        }

        recoveredPackets++;
        recoveredBytes += decodedPacket.Length;
        nextExpectedPacket++;
    }

    var expectedRecoveredPackets = recoverablePackets.Count(recoverable => recoverable);
    if (recoveredPackets != expectedRecoveredPackets)
    {
        throw new InvalidOperationException(
            $"Recovered packet count mismatch: expected {expectedRecoveredPackets}, got {recoveredPackets}.");
    }

    return recoveredBytes;
}

static byte[][] CreateDeterministicPackets(int packetCount, int packetLength)
{
    var packets = new byte[packetCount][];
    var random = new FastRandom(0x5041_434B_4554_3134UL);
    for (var i = 0; i < packets.Length; i++)
    {
        packets[i] = new byte[packetLength];
        random.NextBytes(packets[i]);
    }

    return packets;
}

static List<(int PacketIndex, byte[] Frame)> EncodeLossSweepFrames(IReadOnlyList<byte[]> packets, LinkerFecOptions options)
{
    var frames = new List<(int PacketIndex, byte[] Frame)>(packets.Count * (1 + options.RepairSymbolsPerBlock));
    using var encoder = new LinkerFecCodec(options);
    var packetBuffer = new byte[GetEncodedPacketSize(packets.Count == 0 ? sizeof(int) : packets.Max(packet => packet.Length) + sizeof(int), options)];

    for (var packetIndex = 0; packetIndex < packets.Count; packetIndex++)
    {
        var packet = packets[packetIndex];
        var record = CreatePacketRecord(packet);
        if (!encoder.TryEncodePacket(
                record.AsSpan(),
                packetBuffer.AsSpan(),
                out var bytesWritten,
                out _,
                isFinalPacket: packetIndex == packets.Count - 1))
        {
            throw new InvalidOperationException($"Encode failed for packet {packetIndex}.");
        }

        AddLengthPrefixedFramesWithPacketIndex(packetBuffer.AsSpan(0, bytesWritten), packetIndex, frames);
    }

    return frames;
}

static void AddLengthPrefixedFramesWithPacketIndex(
    ReadOnlySpan<byte> packet,
    int packetIndex,
    List<(int PacketIndex, byte[] Frame)> frames)
{
    var offset = 0;
    while (offset < packet.Length)
    {
        var frameLength = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        frames.Add((packetIndex, packet.Slice(offset, frameLength).ToArray()));
        offset += frameLength;
    }
}

static void RunRandomRoundTripStress(long iterations)
{
    var options = new LinkerFecOptions
    {
        SymbolSize = 1400,
        SourceSymbolsPerBlock = 2,
        RepairSymbolsPerBlock = 1,
        MaxDecoderBlocks = 1024
    };

    var raw = new byte[1400];
    var record = new byte[sizeof(int) + raw.Length];
    var encoded = new byte[GetEncodedPacketSize(record.Length, options)];
    var decoded = new byte[options.MaxDecodeBufferSize];
    var random = new FastRandom(0x8A5C_2D19_7E4B_3F01UL);
    using var encoder = new LinkerFecCodec(options);
    using var decoder = new LinkerFecCodec(options);

    var rawBytes = 0L;
    var encodedBytes = 0L;
    var nextReport = Math.Min(iterations, 1_000_000L);
    var reportStep = Math.Max(1_000_000L, iterations / 100);
    var watch = Stopwatch.StartNew();

    Console.WriteLine(
        $"Random round-trip stress: {iterations:N0} packets, length 10-1400, " +
        $"symbol={options.SymbolSize}, source={options.SourceSymbolsPerBlock}, repair={options.RepairSymbolsPerBlock}");

    for (var i = 0L; i < iterations; i++)
    {
        var rawLength = random.NextInt32(10, 1401);
        var rawPacket = raw.AsSpan(0, rawLength);
        random.NextBytes(rawPacket);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, sizeof(int)), rawLength);
        rawPacket.CopyTo(record.AsSpan(sizeof(int), rawLength));
        var packetRecord = record.AsSpan(0, sizeof(int) + rawLength);

        if (!encoder.TryEncodePacket(
                packetRecord,
                encoded.AsSpan(),
                out var bytesWritten,
                out var packetCount,
                isFinalPacket: i + 1 == iterations))
        {
            throw new InvalidOperationException($"Encode failed at packet {i:N0}, raw length {rawLength}.");
        }

        var offset = 0;
        var decodedCount = 0;
        while (offset < bytesWritten)
        {
            if (bytesWritten - offset < sizeof(int))
            {
                throw new InvalidOperationException($"Truncated length prefix at packet {i:N0}, offset {offset}.");
            }

            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(offset, sizeof(int)));
            offset += sizeof(int);
            if (frameLength <= 0 || frameLength > bytesWritten - offset)
            {
                throw new InvalidOperationException(
                    $"Invalid frame length {frameLength} at packet {i:N0}, offset {offset}.");
            }

            if (decoder.TryDecodeFrame(encoded.AsSpan(offset, frameLength), decoded.AsSpan(), out var decodedLength))
            {
                decodedCount++;
                if (decodedLength != packetRecord.Length)
                {
                    throw new InvalidOperationException(
                        $"Decoded length mismatch at packet {i:N0}: expected {packetRecord.Length}, got {decodedLength}.");
                }

                if (!packetRecord.SequenceEqual(decoded.AsSpan(0, decodedLength)))
                {
                    throw new InvalidOperationException($"Decoded record mismatch at packet {i:N0}.");
                }

                var decodedPackets = ParsePacketRecords(decoded.AsMemory(0, decodedLength));
                if (decodedPackets.Count != 1 || !rawPacket.SequenceEqual(decodedPackets[0]))
                {
                    throw new InvalidOperationException($"Decoded payload mismatch at packet {i:N0}.");
                }
            }

            offset += frameLength;
        }

        if (offset != bytesWritten)
        {
            throw new InvalidOperationException($"Encoded packet parse ended at {offset}, expected {bytesWritten}.");
        }

        if (decodedCount != 1)
        {
            throw new InvalidOperationException($"Expected exactly one decoded payload at packet {i:N0}, got {decodedCount}.");
        }

        var expectedPacketCount = ((packetRecord.Length + options.SymbolSize - 1) / options.SymbolSize) + options.RepairSymbolsPerBlock;
        if (packetCount != expectedPacketCount)
        {
            throw new InvalidOperationException($"Expected {expectedPacketCount} FEC frames at packet {i:N0}, got {packetCount}.");
        }

        rawBytes += rawLength;
        encodedBytes += bytesWritten;

        var processed = i + 1;
        if (processed == nextReport)
        {
            PrintStressProgress(processed, iterations, rawBytes, encodedBytes, watch.Elapsed);
            nextReport = Math.Min(iterations, nextReport + reportStep);
        }
    }

    watch.Stop();
    PrintStressProgress(iterations, iterations, rawBytes, encodedBytes, watch.Elapsed);
    Console.WriteLine("Random round-trip stress passed.");
}

static void PrintStressProgress(long processed, long total, long rawBytes, long encodedBytes, TimeSpan elapsed)
{
    var packetsPerSecond = processed / Math.Max(elapsed.TotalSeconds, 0.000_001d);
    var rawGbps = rawBytes * 8d / Math.Max(elapsed.TotalSeconds, 0.000_001d) / 1_000_000_000d;
    var overhead = encodedBytes / Math.Max(1d, rawBytes);
    Console.WriteLine(
        $"{processed,12:N0}/{total:N0} packets  " +
        $"{packetsPerSecond,10:N0} pkt/s  {rawGbps,6:N2} raw Gbps  " +
        $"encoded/raw {overhead:N2}x  elapsed {elapsed}");
}

internal struct FastRandom
{
    private ulong _state;

    public FastRandom(ulong seed)
    {
        _state = seed == 0 ? 0x9E37_79B9_7F4A_7C15UL : seed;
    }

    public int NextInt32(int minInclusive, int maxExclusive)
    {
        var range = (uint)(maxExclusive - minInclusive);
        return minInclusive + (int)(NextUInt32() % range);
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

    private uint NextUInt32()
    {
        return (uint)(NextUInt64() >> 32);
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
