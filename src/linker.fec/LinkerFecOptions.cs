namespace linker.fec;

public enum LinkerFecRepairGenerationMode
{
    Auto,
    SourceCoefficients,
    IntermediateSymbols
}

public sealed class LinkerFecOptions
{
    public const int MinSymbolSize = 64;
    public const int MaxSymbolSize = 65_535;
    public const int MinSourceSymbolsPerBlock = 1;
    public const int MaxSourceSymbolsPerBlock = byte.MaxValue;
    public const int MinRepairSymbolsPerBlock = 1;
    public const int MaxRepairSymbolsPerBlock = byte.MaxValue;
    public const int MaxSymbolsPerBlock = byte.MaxValue + 1;

    public int SymbolSize { get; init; } = 1440;
    public int SourceSymbolsPerBlock { get; init; } = 2;
    public int RepairSymbolsPerBlock { get; init; } = 1;
    public int MinimumRepairSymbolsPerEncodedBlock { get; init; } = 1;
    public int MaxDecoderBlocks { get; init; } = 256;
    public int MaxSkipBlocks { get; init; } = 10;
    public LinkerFecRepairGenerationMode RepairGenerationMode { get; init; } = LinkerFecRepairGenerationMode.Auto;

    public int BlockSize => checked(SymbolSize * SourceSymbolsPerBlock);

    public int MaxEncodeBufferSize => checked(
        SourceSymbolsPerBlock * (sizeof(int) + LinkerFecEncodedSymbol.HeaderSize + SymbolSize) +
        RepairSymbolsPerBlock * (sizeof(int) + LinkerFecEncodedSymbol.HeaderSize + sizeof(ushort) + SymbolSize));

    public int MaxDecodeBufferSize => checked((SymbolSize + sizeof(int)) * SourceSymbolsPerBlock);

    public int MaxRecordListSize => MaxDecodeBufferSize;

    public int GetRepairSymbolsForSourceCount(int sourceSymbolCount)
    {
        if (sourceSymbolCount is < MinSourceSymbolsPerBlock or > MaxSourceSymbolsPerBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSymbolCount), sourceSymbolCount,
                $"Source symbol count must be in [{MinSourceSymbolsPerBlock}, {MaxSourceSymbolsPerBlock}].");
        }

        if (sourceSymbolCount > SourceSymbolsPerBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSymbolCount), sourceSymbolCount,
                "Source symbol count cannot exceed the configured source symbols per block.");
        }

        var proportionalRepairCount = checked(
            ((sourceSymbolCount * RepairSymbolsPerBlock) + SourceSymbolsPerBlock - 1) / SourceSymbolsPerBlock);
        return Math.Min(
            RepairSymbolsPerBlock,
            Math.Max(MinimumRepairSymbolsPerEncodedBlock, proportionalRepairCount));
    }

    internal void Validate()
    {
        if (SymbolSize is < MinSymbolSize or > MaxSymbolSize)
        {
            throw new ArgumentOutOfRangeException(nameof(SymbolSize), SymbolSize,
                $"Symbol size must be in [{MinSymbolSize}, {MaxSymbolSize}].");
        }

        if (SourceSymbolsPerBlock is < MinSourceSymbolsPerBlock or > MaxSourceSymbolsPerBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(SourceSymbolsPerBlock), SourceSymbolsPerBlock,
                $"Source symbol count must be in [{MinSourceSymbolsPerBlock}, {MaxSourceSymbolsPerBlock}].");
        }

        if (RepairSymbolsPerBlock is < MinRepairSymbolsPerBlock or > MaxRepairSymbolsPerBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(RepairSymbolsPerBlock), RepairSymbolsPerBlock,
                $"Repair symbol count must be in [{MinRepairSymbolsPerBlock}, {MaxRepairSymbolsPerBlock}].");
        }

        if (MinimumRepairSymbolsPerEncodedBlock is < MinRepairSymbolsPerBlock or > MaxRepairSymbolsPerBlock)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumRepairSymbolsPerEncodedBlock),
                MinimumRepairSymbolsPerEncodedBlock,
                $"Minimum repair symbol count must be in [{MinRepairSymbolsPerBlock}, {MaxRepairSymbolsPerBlock}].");
        }

        if (MinimumRepairSymbolsPerEncodedBlock > RepairSymbolsPerBlock)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumRepairSymbolsPerEncodedBlock),
                MinimumRepairSymbolsPerEncodedBlock,
                "Minimum repair symbol count cannot exceed the configured repair symbols per block.");
        }

        if (SourceSymbolsPerBlock + RepairSymbolsPerBlock > MaxSymbolsPerBlock)
        {
            throw new ArgumentOutOfRangeException(nameof(RepairSymbolsPerBlock),
                "The compact frame format supports at most 256 total source and repair symbols per block.");
        }

        if ((long)(SymbolSize + sizeof(int)) * SourceSymbolsPerBlock > LinkerFecEncodedSymbol.MaxBlockLength)
        {
            throw new ArgumentOutOfRangeException(nameof(SourceSymbolsPerBlock),
                "The configured decoded record list is larger than the compact frame block length limit.");
        }

        if ((long)(SymbolSize + sizeof(int)) * SourceSymbolsPerBlock > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(SourceSymbolsPerBlock),
                "The configured decoded record list is larger than a single .NET byte array.");
        }

        if (MaxDecoderBlocks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDecoderBlocks), MaxDecoderBlocks,
                "The decoder block limit must be positive.");
        }

        if (MaxSkipBlocks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSkipBlocks), MaxSkipBlocks,
                "The decoder skip window must be positive.");
        }

        if (MaxSkipBlocks > MaxDecoderBlocks)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSkipBlocks), MaxSkipBlocks,
                "The decoder skip window cannot exceed the decoder block limit.");
        }

        if (!Enum.IsDefined(RepairGenerationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(RepairGenerationMode), RepairGenerationMode,
                "Invalid repair generation mode.");
        }
    }
}
