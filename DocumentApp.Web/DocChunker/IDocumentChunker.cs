using DocumentApp.Web.DocChunker;

namespace DocumentApp.Web.DocChunker;

/// <summary>
/// Turns a source document into retrievable chunks.
/// One implementation per format (.docx, .pdf, .md ...); resolve with
/// <c>IEnumerable&lt;IDocumentChunker&gt;</c> and pick via <see cref="CanHandle"/>.
/// </summary>
public interface IDocumentChunker
{
    /// <summary>File extensions this implementation understands, lowercase and dot-prefixed.</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }

    bool CanHandle(string fileName);

    /// <param name="stream">Document content. Not disposed by the chunker.</param>
    /// <param name="fileName">Original file name, recorded in chunk metadata.</param>
    Task<IReadOnlyList<DocumentChunk>> ChunkAsync(
        Stream stream,
        string fileName,
        ChunkingOptions? options = null,
        CancellationToken cancellationToken = default);
}
