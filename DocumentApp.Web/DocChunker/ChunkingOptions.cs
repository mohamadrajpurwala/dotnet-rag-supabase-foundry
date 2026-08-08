namespace DocumentApp.Web.DocChunker;

public sealed class ChunkingOptions
{
    /// <summary>Hard upper bound on chunk length in characters.</summary>
    public int MaxChars { get; set; } = 2000;

    /// <summary>How many trailing characters of a chunk are repeated at the start of the next one.</summary>
    public int OverlapChars { get; set; } = 200;

    /// <summary>
    /// Chunks shorter than this are not flushed on their own; they keep accumulating.
    /// Prevents a document full of short headings from producing dozens of useless chunks.
    /// </summary>
    public int MinChars { get; set; } = 200;

    /// <summary>
    /// Start a new chunk whenever a heading at or above <see cref="SplitHeadingLevel"/> is met.
    /// Turn off for a pure sliding-window split.
    /// </summary>
    public bool SplitOnHeadings { get; set; } = true;

    /// <summary>Headings of this level or shallower force a chunk boundary (1 = only H1, 2 = H1+H2...).</summary>
    public int SplitHeadingLevel { get; set; } = 2;

    /// <summary>Render tables as Markdown pipe tables and include them in the output.</summary>
    public bool IncludeTables { get; set; } = true;

    /// <summary>Prefix list paragraphs with "- " so bullets survive into the chunk text.</summary>
    public bool PreserveListMarkers { get; set; } = true;

    /// <summary>
    /// Group words into blocks and detect reading order before extracting.
    /// Required for multi-column PDFs, otherwise columns interleave line by line.
    /// Costs noticeably more CPU.
    /// </summary>
    public bool PdfLayoutAware { get; set; } = true;

    /// <summary>Guess headings from font size. Heuristic — see PdfChunker.</summary>
    public bool PdfInferHeadings { get; set; } = true;

    /// <summary>For password-protected PDFs.</summary>
    public string? PdfPassword { get; set; }

    public void Validate()
    {
        if (MaxChars < 100)
            throw new ArgumentOutOfRangeException(nameof(MaxChars), "MaxChars must be at least 100.");
        if (OverlapChars < 0 || OverlapChars >= MaxChars)
            throw new ArgumentOutOfRangeException(nameof(OverlapChars), "OverlapChars must be >= 0 and < MaxChars.");
        if (MinChars < 0 || MinChars > MaxChars)
            throw new ArgumentOutOfRangeException(nameof(MinChars), "MinChars must be between 0 and MaxChars.");
        if (SplitHeadingLevel is < 1 or > 9)
            throw new ArgumentOutOfRangeException(nameof(SplitHeadingLevel), "SplitHeadingLevel must be between 1 and 9.");
    }
}
