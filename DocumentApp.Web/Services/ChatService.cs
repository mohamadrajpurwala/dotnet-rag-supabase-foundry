using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using System.Text;

namespace DocumentApp.Web.Services;

public class ChatService(
    IngestionService ingestion,
    IChatClient chat,
    ILogger<ChatService> logger)
{
    private const double MinSimilarity = 0.35;

    private const int MaxContextChars = 8000;

    private const string SystemPrompt = """
        You answer questions about internal company documents.

        Rules:
        - Use ONLY the numbered sources provided. Never use outside knowledge.
        - Cite the source number in square brackets after each claim, like [1] or [2][3].
        - Quote figures, dates, names and durations EXACTLY as they appear. Do not round or rephrase them.
        - If the sources do not answer the question, say so plainly. Do not guess.
        - Be concise: two to four sentences, unless the question genuinely needs a list.
        """;

    public async Task<RagAnswer> AskAsync(string question, CancellationToken ct = default)
    {
        var sources = await RetrieveAsync(question, ct);

        if (sources.Count == 0)
            return new RagAnswer(NoResultsMessage, [], false);

        var response = await chat.GetResponseAsync(
            BuildMessages(question, sources),
            new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 600 },
            ct);

        return new RagAnswer(response.Text ?? "", sources, true);
    }

    public async Task<List<SearchHit>> RetrieveAsync(string question, CancellationToken ct = default)
    {
        var hits = await ingestion.SearchAsync(question, limit: 5, ct);

        var relevant = hits.Where(h => h.Similarity >= MinSimilarity).ToList();

        if (relevant.Count == 0 && hits.Count > 0)
            logger.LogInformation(
                "Rejected {Count} hits for {Question}; best similarity was {Score:F2}",
                hits.Count, question, hits[0].Similarity);

        return relevant;
    }

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        string question,
        IReadOnlyList<SearchHit> sources,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (sources.Count == 0)
        {
            yield return NoResultsMessage;
            yield break;
        }

        var options = new ChatOptions { Temperature = 0.1f, MaxOutputTokens = 600 };

        await foreach (var update in chat.GetStreamingResponseAsync(
                           BuildMessages(question, sources), options, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    private static List<ChatMessage> BuildMessages(string question, IReadOnlyList<SearchHit> sources) =>
    [
        new(ChatRole.System, SystemPrompt),
        new(ChatRole.User, $"{BuildContext(sources)}\n\nQuestion: {question}")
    ];

    private static string BuildContext(IReadOnlyList<SearchHit> sources)
    {
        var sb = new StringBuilder();
        var used = 0;

        for (var i = 0; i < sources.Count; i++)
        {
            var hit = sources[i];
            if (used + hit.Content.Length > MaxContextChars) break;

            sb.AppendLine($"[{i + 1}] File: {hit.FileName}");
            if (hit.PageNumber is int page)
                sb.AppendLine($"    Page: {page}");
            if (!string.IsNullOrEmpty(hit.HeadingPath))
                sb.AppendLine($"    Section: {hit.HeadingPath}");
            sb.AppendLine();
            sb.AppendLine(hit.Content);
            sb.AppendLine("\n---\n");

            used += hit.Content.Length;
        }

        return sb.ToString();
    }

    private const string NoResultsMessage =
        "I couldn't find anything relevant in the uploaded documents. Try rephrasing, or check that the right document has been ingested.";
}

public record RagAnswer(string Answer, IReadOnlyList<SearchHit> Sources, bool Grounded);