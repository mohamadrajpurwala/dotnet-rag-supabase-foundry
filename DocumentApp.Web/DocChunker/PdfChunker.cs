using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DocumentApp.Web.DocChunker;

public sealed class PdfChunker : IDocumentChunker
{
    private static readonly string[] Extensions = [".pdf"];

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public bool CanHandle(string fileName) =>
        Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<DocumentChunk>> ChunkAsync(
        Stream stream,
        string fileName,
        ChunkingOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new ChunkingOptions();
        options.Validate();

        Stream working = stream;
        MemoryStream? buffer = null;
        if (!stream.CanSeek)
        {
            buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            working = buffer;
        }

        try
        {
            var blocks = ReadBlocks(working, options, cancellationToken);
            return Assemble(blocks, fileName, options, cancellationToken);
        }
        finally
        {
            if (buffer is not null) await buffer.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- reading

    private sealed record PdfBlock(string Text, int PageNumber, bool IsHeading);

    private static List<PdfBlock> ReadBlocks(Stream stream, ChunkingOptions options, CancellationToken ct)
    {
        var blocks = new List<PdfBlock>();

        // UseLenientParsing keeps going on slightly malformed files, which is most
        // PDFs produced by scanners and older government systems.
        var parsingOptions = new ParsingOptions
        {
            UseLenientParsing = true,
            Password = options.PdfPassword ?? string.Empty
        };

        using var doc = PdfDocument.Open(stream, parsingOptions);

        var totalChars = 0;

        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            if (options.PdfLayoutAware)
            {
                // Docstrum groups words into blocks by spacing, then the reading-order
                // detector sorts them. This is what makes two-column layouts come out
                // in the right order instead of interleaved line by line.
                // Costs real CPU — noticeably slower on dense pages.
                var words = page.GetWords().ToList();
                if (words.Count == 0) continue;

                var textBlocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                var ordered = UnsupervisedReadingOrderDetector.Instance.Get(textBlocks);

                var bodySize = MedianFontSize(textBlocks);

                foreach (var block in ordered)
                {
                    var text = NormalizeWhitespace(block.Text);
                    if (text.Length == 0) continue;

                    totalChars += text.Length;
                    blocks.Add(new PdfBlock(text, page.Number, IsHeadingBlock(block, text, bodySize, options)));
                }
            }
            else
            {
                // Cheaper path: content-stream order. Fine for single-column documents.
                var text = NormalizeWhitespace(ContentOrderTextExtractor.GetText(page));
                if (text.Length == 0) continue;

                totalChars += text.Length;
                blocks.Add(new PdfBlock(text, page.Number, false));
            }
        }

        // A scanned PDF parses perfectly and yields almost nothing. Without this
        // check you get zero chunks and a confusing "no text extracted" error,
        // rather than the actual diagnosis.
        var pageCount = doc.NumberOfPages;
        if (pageCount > 0 && totalChars / pageCount < 50)
        {
            throw new InvalidDataException(
                $"Extracted only {totalChars} characters across {pageCount} pages. " +
                "This PDF is almost certainly scanned images rather than text — it needs OCR before ingestion.");
        }

        return blocks;
    }

    private static double MedianFontSize(IReadOnlyList<TextBlock> blocks)
    {
        var sizes = blocks
            .SelectMany(b => b.TextLines)
            .SelectMany(l => l.Words)
            .SelectMany(w => w.Letters)
            .Select(l => l.PointSize)
            .Where(s => s > 0)
            .OrderBy(s => s)
            .ToList();

        return sizes.Count == 0 ? 0 : sizes[sizes.Count / 2];
    }

    /// <summary>
    /// Heuristic, and it will be wrong sometimes. A heading is short, and set in
    /// noticeably larger type than the body. Bold-only headings at body size are
    /// missed; pull-quotes in large type are false positives.
    /// </summary>
    private static bool IsHeadingBlock(TextBlock block, string text, double bodySize, ChunkingOptions options)
    {
        if (!options.PdfInferHeadings || bodySize <= 0) return false;
        if (text.Length > 120) return false;

        var sizes = block.TextLines
            .SelectMany(l => l.Words)
            .SelectMany(w => w.Letters)
            .Select(l => l.PointSize)
            .Where(s => s > 0)
            .ToList();

        if (sizes.Count == 0) return false;

        return sizes.Average() >= bodySize * 1.15;
    }

    // -------------------------------------------------------------- assembling

    private static List<DocumentChunk> Assemble(
        List<PdfBlock> blocks, string fileName, ChunkingOptions options, CancellationToken ct)
    {
        var chunks = new List<DocumentChunk>();
        var sb = new StringBuilder();
        var chunkPage = 0;
        var currentHeading = string.Empty;

        void Flush(bool carryOverlap)
        {
            var content = sb.ToString().Trim();
            if (content.Length == 0) { sb.Clear(); return; }

            chunks.Add(new DocumentChunk
            {
                Index = chunks.Count,
                Content = content,
                HeadingPath = currentHeading,
                PageNumber = chunkPage,
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = fileName,
                    ["page"] = chunkPage.ToString()
                }
            });

            sb.Clear();
            if (carryOverlap && options.OverlapChars > 0)
            {
                var tail = TakeTail(content, options.OverlapChars);
                if (tail.Length > 0) sb.Append(tail).Append("\n\n");
            }
        }

        foreach (var block in blocks)
        {
            ct.ThrowIfCancellationRequested();

            if (sb.Length == 0) chunkPage = block.PageNumber;

            if (block.IsHeading)
            {
                if (sb.Length >= options.MinChars) Flush(carryOverlap: false);
                currentHeading = block.Text;
                if (sb.Length == 0) chunkPage = block.PageNumber;
            }

            foreach (var piece in SplitOversized(block.Text, options.MaxChars))
            {
                var projected = sb.Length + (sb.Length > 0 ? 2 : 0) + piece.Length;
                if (projected > options.MaxChars && sb.Length >= options.MinChars)
                {
                    Flush(carryOverlap: true);
                    chunkPage = block.PageNumber;
                }

                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(piece);
            }
        }

        Flush(carryOverlap: false);
        return chunks;
    }

    // ---- these three are copied from DocxChunker. See the note in the README:
    // ---- worth extracting into a shared ChunkAssembler once both are stable.

    private static IEnumerable<string> SplitOversized(string text, int maxChars)
    {
        if (text.Length <= maxChars) { yield return text; }

        var sentences = text.Split(". ", StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();

        foreach (var sentence in sentences)
        {
            var part = sentence;

            while (part.Length > maxChars)
            {
                var cut = part.LastIndexOf(' ', maxChars - 1);
                if (cut <= 0) cut = maxChars;
                if (sb.Length > 0) { yield return sb.ToString().Trim(); sb.Clear(); }
                yield return part[..cut].Trim();
                part = part[cut..].TrimStart();
            }

            if (sb.Length + part.Length + 2 > maxChars && sb.Length > 0)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
            }

            if (sb.Length > 0) sb.Append(". ");
            sb.Append(part);
        }

        if (sb.Length > 0) yield return sb.ToString().Trim();
    }

    private static string TakeTail(string text, int overlapChars)
    {
        var take = Math.Min(overlapChars, text.Length / 2);
        if (take <= 0) return string.Empty;

        var tail = text[^take..];
        var boundary = tail.IndexOfAny(['.', '!', '?', '\n']);
        if (boundary >= 0 && boundary < tail.Length - 1)
            tail = tail[(boundary + 1)..];

        return tail.Trim();
    }

    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // PDFs hyphenate across line breaks. Rejoining prevents "auth-" and
        // "orisation" being embedded as two meaningless fragments.
        text = text.Replace("-\n", "").Replace("-\r\n", "");

        return System.Text.RegularExpressions.Regex
            .Replace(text.Replace('\u00A0', ' '), @"\s+", " ")
            .Trim();
    }
}