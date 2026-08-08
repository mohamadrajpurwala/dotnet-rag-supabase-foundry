namespace DocumentApp.Web.DocChunker;

/// <summary>
/// A single retrievable unit of text produced from a source document.
/// </summary>
public sealed record DocumentChunk
{
    /// <summary>Zero-based position of this chunk within the document.</summary>
    public required int Index { get; init; }

    /// <summary>The chunk text, ready to be embedded.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// Breadcrumb of the heading hierarchy this chunk sits under,
    /// e.g. "Employee Handbook &gt; Leave Policy &gt; Annual Leave".
    /// Empty when the document has no headings.
    /// </summary>
    public string HeadingPath { get; init; } = string.Empty;

    /// <summary>Deepest heading level covering this chunk (1-9, or 0 when none).</summary>
    public int HeadingLevel { get; init; }

    public int CharCount => Content.Length;

    /// <summary>Rough token estimate (~4 chars/token). Replace with a real tokenizer if you need precision.</summary>
    public int EstimatedTokenCount => (int)Math.Ceiling(Content.Length / 4.0);

    /// <summary>Free-form metadata to persist alongside the vector (source file, page, tags...).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Text prefixed with its heading breadcrumb. Embedding this instead of <see cref="Content"/>
    /// usually improves retrieval, because an isolated paragraph loses its context.
    /// </summary>
    public string ToEmbeddingText() =>
        string.IsNullOrEmpty(HeadingPath) ? Content : $"{HeadingPath}\n\n{Content}";

    public int? PageNumber { get; init; }
}
