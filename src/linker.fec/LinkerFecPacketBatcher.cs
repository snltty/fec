using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace linker.fec;

/// <summary>
/// Opportunistically batches small application packets into
/// [4-byte little-endian length][packet] records and FEC-encodes the current
/// complete record set without adding an explicit wait timer. Each batch is
/// capped by both the FEC payload block size and SourceSymbolsPerBlock
/// application packets.
/// </summary>
public sealed class LinkerFecPacketBatcher : IDisposable
{
    private const int LengthPrefixSize = sizeof(int);
    private const int MinimumPipeSegmentSize = 64 * 1024;

    private readonly long _maxRemaining;
    private readonly Pipe _pipe;
    private readonly byte[] _batchBuffer;
    private readonly byte[] _encodedBuffer;
    private readonly LinkerFecCodec _codec;
    private long _sendRemaining;
    private bool _disposed;

    public LinkerFecPacketBatcher(
        long maxRemaining,
        int sourceSymbolsPerBlock = 10,
        int repairSymbolsPerBlock = 2)
        : this(maxRemaining, new LinkerFecOptions
        {
            SourceSymbolsPerBlock = sourceSymbolsPerBlock,
            RepairSymbolsPerBlock = repairSymbolsPerBlock
        })
    {
    }

    public LinkerFecPacketBatcher(long maxRemaining, LinkerFecOptions? options)
    {
        if (maxRemaining <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRemaining), maxRemaining, "Buffer limit must be positive.");
        }

        _maxRemaining = maxRemaining;
        Options = options ?? new LinkerFecOptions();
        Options.Validate();
        _batchBuffer = new byte[Options.MaxDecodeBufferSize];
        _encodedBuffer = new byte[GetMaxEncodedPacketSize(Options)];
        _codec = new LinkerFecCodec(Options);
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: maxRemaining,
            resumeWriterThreshold: maxRemaining / 2,
            useSynchronizationContext: false,
            minimumSegmentSize: GetPipeSegmentSize(Options.MaxDecodeBufferSize)));
    }

    public LinkerFecOptions Options { get; }

    public long SendBufferRemaining => Volatile.Read(ref _sendRemaining);

    public long SendBufferFree => _maxRemaining - SendBufferRemaining;

    public long SendBytes { get; private set; }

    public bool IsCompleted { get; private set; }

    public int MaxEncodedPacketSize => _encodedBuffer.Length;

    public int LastRawBytes { get; private set; }

    public int LastRawPacketCount { get; private set; }

    public int LastEncodedFrameCount { get; private set; }

    /// <summary>
    /// Writes one application packet. The batcher stores it as
    /// [4-byte little-endian length][packet] before FEC batch encoding.
    /// </summary>
    public ValueTask<FlushResult> WriteAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (packet.Length > Options.SymbolSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(packet),
                packet.Length,
                "A single batched packet payload cannot exceed one configured FEC source symbol.");
        }

        var recordLength = checked(LengthPrefixSize + packet.Length);
        var destination = _pipe.Writer.GetMemory(recordLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Span, packet.Length);
        packet.CopyTo(destination.Slice(LengthPrefixSize));
        _pipe.Writer.Advance(recordLength);
        Interlocked.Add(ref _sendRemaining, recordLength);

        return _pipe.Writer.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the currently available complete application packets, FEC-encodes
    /// them, and returns one or more [4-byte frame length][FEC frame] records.
    /// The returned memory is valid until the next ReadAsync call.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (true)
        {
            var result = await _pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (buffer.IsEmpty && result.IsCompleted)
            {
                IsCompleted = true;
                return ReadOnlyMemory<byte>.Empty;
            }

            var batchLength = CopyCompletePacketBatch(
                buffer,
                out var rawPacketCount,
                out var consumed,
                out var examined);

            if (batchLength == 0)
            {
                if (result.IsCompleted)
                {
                    throw new InvalidDataException("Batched packet input ended with an incomplete length-prefixed packet.");
                }

                _pipe.Reader.AdvanceTo(consumed, examined);
                continue;
            }

            var isFinalPacket = result.IsCompleted && buffer.Slice(consumed).IsEmpty;
            _pipe.Reader.AdvanceTo(consumed);
            Interlocked.Add(ref _sendRemaining, -batchLength);
            SendBytes += batchLength;

            var bytesWritten = _codec.EncodePacket(
                _batchBuffer.AsSpan(0, batchLength),
                _encodedBuffer.AsSpan(),
                out var packetCount,
                isFinalPacket);

            LastRawBytes = batchLength;
            LastRawPacketCount = rawPacketCount;
            LastEncodedFrameCount = packetCount;
            return _encodedBuffer.AsMemory(0, bytesWritten);
        }
    }

    public void Complete(Exception? exception = null)
    {
        ThrowIfDisposed();
        if (exception is null)
        {
            _pipe.Writer.Complete();
        }
        else
        {
            _pipe.Writer.Complete(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pipe.Writer.Complete();
        _pipe.Reader.Complete();
        _codec.Dispose();
    }

    private int CopyCompletePacketBatch(
        in ReadOnlySequence<byte> input,
        out int packetCount,
        out SequencePosition consumed,
        out SequencePosition examined)
    {
        packetCount = 0;
        consumed = input.Start;
        examined = input.End;

        var remaining = input;
        var batchLength = 0;
        var batchPayloadLength = 0;

        while (remaining.Length >= LengthPrefixSize)
        {
            if (packetCount >= Options.SourceSymbolsPerBlock)
            {
                break;
            }

            var packetLength = ReadInt32LittleEndian(remaining.Slice(0, LengthPrefixSize));
            if (packetLength < 0)
            {
                throw new InvalidDataException("Batched packet length cannot be negative.");
            }

            var recordLength = checked((long)LengthPrefixSize + packetLength);
            if (packetLength > Options.SymbolSize)
            {
                throw new InvalidDataException("A single batched packet payload exceeds the configured FEC symbol size.");
            }

            if (packetLength > Options.BlockSize)
            {
                throw new InvalidDataException("A single batched packet payload exceeds the configured FEC block size.");
            }

            if (remaining.Length < recordLength)
            {
                break;
            }

            if (batchPayloadLength > 0 && batchPayloadLength + packetLength > Options.BlockSize)
            {
                break;
            }

            remaining.Slice(0, recordLength).CopyTo(_batchBuffer.AsSpan(batchLength, (int)recordLength));
            batchLength += (int)recordLength;
            batchPayloadLength += packetLength;
            packetCount++;
            remaining = remaining.Slice(recordLength);
        }

        consumed = input.GetPosition(batchLength);
        examined = batchLength == 0 ? input.End : consumed;
        return batchLength;
    }

    private static int ReadInt32LittleEndian(in ReadOnlySequence<byte> source)
    {
        if (source.FirstSpan.Length >= LengthPrefixSize)
        {
            return BinaryPrimitives.ReadInt32LittleEndian(source.FirstSpan);
        }

        Span<byte> header = stackalloc byte[LengthPrefixSize];
        source.CopyTo(header);
        return BinaryPrimitives.ReadInt32LittleEndian(header);
    }

    private static int GetMaxEncodedPacketSize(LinkerFecOptions options)
    {
        return options.MaxEncodeBufferSize;
    }

    private static int GetPipeSegmentSize(int blockSize)
    {
        return Math.Max(blockSize, MinimumPipeSegmentSize);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
