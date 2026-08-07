namespace DocumentApp.Web.DocChunker;

public interface IDocumentChunkerResolver
{
    IDocumentChunker Resolve(string fileName);
    bool TryResolve(string fileName, out IDocumentChunker chunker);
}

internal sealed class DocumentChunkerResolver(IEnumerable<IDocumentChunker> chunkers) : IDocumentChunkerResolver
{
    private readonly IReadOnlyList<IDocumentChunker> _chunkers = [.. chunkers];

    public bool TryResolve(string fileName, out IDocumentChunker chunker)
    {
        chunker = _chunkers.FirstOrDefault(c => c.CanHandle(fileName))!;
        return chunker is not null;
    }

    public IDocumentChunker Resolve(string fileName) =>
        TryResolve(fileName, out var chunker)
            ? chunker
            : throw new NotSupportedException(
                $"No chunker registered for '{Path.GetExtension(fileName)}'. " +
                $"Supported: {string.Join(", ", _chunkers.SelectMany(c => c.SupportedExtensions).Distinct())}");
}

public static class ChunkingServiceCollectionExtensions
{
    public static IServiceCollection AddDocumentChunking(this IServiceCollection services)
    {
        services.AddSingleton<IDocumentChunker, DocxChunker>();
        services.AddSingleton<IDocumentChunkerResolver, DocumentChunkerResolver>();
        return services;
    }
}
