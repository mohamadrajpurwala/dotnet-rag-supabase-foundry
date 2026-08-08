using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocumentApp.Web.DocChunker;

public sealed partial class DocxChunker : IDocumentChunker
{
    private static readonly string[] Extensions = [".docx"];

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    public bool CanHandle(string fileName) =>
        Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase);

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

    private enum BlockKind { Paragraph, Heading, Table }

    private sealed record Block(string Text, BlockKind Kind, int HeadingLevel);

    private static List<Block> ReadBlocks(Stream stream, ChunkingOptions options, CancellationToken ct)
    {
        var blocks = new List<Block>();

        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body
                   ?? throw new InvalidDataException("The file is not a valid Word document (no main body part).");

        var styleNames = BuildStyleNameMap(doc.MainDocumentPart);

        foreach (var element in body.ChildElements)
        {
            ct.ThrowIfCancellationRequested();

            switch (element)
            {
                case Paragraph paragraph:
                {
                    var text = NormalizeWhitespace(ReadParagraphText(paragraph));
                    if (text.Length == 0) continue;

                    var level = GetHeadingLevel(paragraph, styleNames);
                    if (level > 0)
                    {
                        blocks.Add(new Block(text, BlockKind.Heading, level));
                    }
                    else
                    {
                        if (options.PreserveListMarkers && IsListParagraph(paragraph))
                            text = "- " + text;
                        blocks.Add(new Block(text, BlockKind.Paragraph, 0));
                    }
                    break;
                }

                case Table table when options.IncludeTables:
                {
                    var text = RenderTable(table);
                    if (text.Length > 0) blocks.Add(new Block(text, BlockKind.Table, 0));
                    break;
                }
            }
        }

        return blocks;
    }

    private static Dictionary<string, string> BuildStyleNameMap(MainDocumentPart mainPart)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var styles = mainPart.StyleDefinitionsPart?.Styles;
        if (styles is null) return map;

        foreach (var style in styles.Elements<Style>())
        {
            var id = style.StyleId?.Value;
            var name = style.StyleName?.Val?.Value;
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                map[id] = name;
        }
        return map;
    }

    private static int GetHeadingLevel(Paragraph paragraph, IReadOnlyDictionary<string, string> styleNames)
    {
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;

        if (!string.IsNullOrEmpty(styleId))
        {
            if (styleId.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;

            var match = HeadingStyleRegex().Match(styleId);
            if (match.Success) return int.Parse(match.Groups[1].Value);

            if (styleNames.TryGetValue(styleId, out var name))
            {
                if (name.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
                match = HeadingStyleRegex().Match(name);
                if (match.Success) return int.Parse(match.Groups[1].Value);
            }
        }

        var outline = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        if (outline is >= 0 and <= 8) return outline.Value + 1;

        return 0;
    }

    private static bool IsListParagraph(Paragraph paragraph) =>
        paragraph.ParagraphProperties?.NumberingProperties is not null;

    private static string ReadParagraphText(Paragraph paragraph)
    {
        var sb = new StringBuilder();

        foreach (var run in paragraph.Descendants<Run>())
        {
            if (run.Ancestors<DeletedRun>().Any()) continue;

            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case Text text:      sb.Append(text.Text); break;
                    case TabChar:        sb.Append('\t'); break;
                    case Break:          sb.Append('\n'); break;
                    case NoBreakHyphen:  sb.Append('-'); break;
                    case SoftHyphen:     break;
                }
            }
        }

        return sb.ToString();
    }

    private static string RenderTable(Table table)
    {
        var rows = table.Elements<TableRow>()
            .Select(row => row.Elements<TableCell>()
                .Select(cell => NormalizeWhitespace(cell.InnerText).Replace("|", "\\|"))
                .ToList())
            .Where(cells => cells.Count > 0 && cells.Any(c => c.Length > 0))
            .ToList();

        if (rows.Count == 0) return string.Empty;

        var columns = rows.Max(r => r.Count);
        var sb = new StringBuilder();

        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i];
            sb.Append("| ");
            for (var c = 0; c < columns; c++)
                sb.Append(c < cells.Count ? cells[c] : string.Empty).Append(" | ");
            sb.AppendLine();

            if (i == 0)
            {
                sb.Append('|');
                for (var c = 0; c < columns; c++) sb.Append(" --- |");
                sb.AppendLine();
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static List<DocumentChunk> Assemble(
        List<Block> blocks, string fileName, ChunkingOptions options, CancellationToken ct)
    {
        var chunks = new List<DocumentChunk>();
        var headings = new string[10];
        var buffer = new StringBuilder();
        var bufferPath = string.Empty;
        var bufferLevel = 0;

        void Flush(bool carryOverlap)
        {
            var content = buffer.ToString().Trim();
            if (content.Length == 0) { buffer.Clear(); return; }

            chunks.Add(new DocumentChunk
            {
                Index = chunks.Count,
                Content = content,
                HeadingPath = bufferPath,
                HeadingLevel = bufferLevel,
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = fileName,
                    ["chunkIndex"] = chunks.Count.ToString(),
                }
            });

            buffer.Clear();
            if (carryOverlap && options.OverlapChars > 0)
            {
                var tail = TakeTail(content, options.OverlapChars);
                if (tail.Length > 0) buffer.Append(tail).Append("\n\n");
            }
        }

        foreach (var block in blocks)
        {
            ct.ThrowIfCancellationRequested();

            if (block.Kind == BlockKind.Heading)
            {
                headings[block.HeadingLevel] = block.Text;
                for (var deeper = block.HeadingLevel + 1; deeper < headings.Length; deeper++)
                    headings[deeper] = null!;

                var forcedBreak = options.SplitOnHeadings
                                  && block.HeadingLevel <= options.SplitHeadingLevel
                                  && buffer.Length >= options.MinChars;

                if (forcedBreak) Flush(carryOverlap: false);
            }

            if (buffer.Length == 0 || IsOnlyOverlap(buffer, options))
            {
                bufferPath = BuildPath(headings);
                bufferLevel = DeepestLevel(headings);
            }

            foreach (var piece in SplitOversized(block.Text, options.MaxChars))
            {
                var wouldBe = buffer.Length + (buffer.Length > 0 ? 2 : 0) + piece.Length;
                if (wouldBe > options.MaxChars && buffer.Length >= options.MinChars)
                {
                    Flush(carryOverlap: true);
                    bufferPath = BuildPath(headings);
                    bufferLevel = DeepestLevel(headings);
                }

                if (buffer.Length > 0) buffer.Append("\n\n");
                buffer.Append(piece);
            }
        }

        Flush(carryOverlap: false);
        return chunks;
    }

    private static bool IsOnlyOverlap(StringBuilder buffer, ChunkingOptions options) =>
        buffer.Length > 0 && buffer.Length <= options.OverlapChars + 2;

    private static string BuildPath(string?[] headings) =>
        string.Join(" > ", headings.Where(h => !string.IsNullOrEmpty(h))!);

    private static int DeepestLevel(string?[] headings)
    {
        for (var i = headings.Length - 1; i >= 1; i--)
            if (!string.IsNullOrEmpty(headings[i])) return i;
        return 0;
    }

    private static IEnumerable<string> SplitOversized(string text, int maxChars)
    {
        if (text.Length <= maxChars) { yield return text; }

        var sentences = SentenceRegex().Split(text);
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

            if (sb.Length + part.Length + 1 > maxChars && sb.Length > 0)
            {
                yield return sb.ToString().Trim();
                sb.Clear();
            }

            if (sb.Length > 0) sb.Append(' ');
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
        return WhitespaceRegex().Replace(text.Replace('\u00A0', ' '), " ").Trim();
    }

    [GeneratedRegex(@"^Heading\s*([1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingStyleRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceRegex();
}
