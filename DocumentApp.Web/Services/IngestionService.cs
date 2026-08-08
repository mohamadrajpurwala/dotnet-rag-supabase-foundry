using DocumentApp.Web.DocChunker;
using Microsoft.Extensions.AI;
using Npgsql;
using Pgvector;

namespace DocumentApp.Web.Services
{
    public class IngestionService
    {
        private readonly Supabase.Client _supabase;
        private readonly NpgsqlDataSource _db;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
        private readonly IDocumentChunkerResolver _resolver;
        private readonly ILogger<IngestionService> _logger;

        private const string Bucket = "documents";

        public IngestionService(
            Supabase.Client supabase,
            NpgsqlDataSource db,
            IEmbeddingGenerator<string, Embedding<float>> embedder,
            IDocumentChunkerResolver resolver,
            ILogger<IngestionService> logger)
        {
            _supabase = supabase;
            _db = db;
            _embedder = embedder;
            _resolver = resolver;
            _logger = logger;
        }

        // ---------------------------------------------------------------- ingest

        public async Task<IngestResult> IngestAsync(
            Stream fileStream,
            string fileName,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var startedAt = DateTimeOffset.UtcNow;

            // Blazor Server hands us a non-seekable SignalR stream, and we need the
            // bytes twice (once for Storage, once for OpenXml). So buffer it first.
            progress?.Report("Reading file...");
            using var buffer = new MemoryStream();
            await fileStream.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();
            buffer.Position = 0;

            // Unique path so re-uploading the same name doesn't clobber the old file.
            var storagePath = $"{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}{Path.GetExtension(fileName)}";

            // 1. Row first, so the UI has something to show while the slow work runs.
            var documentId = await InsertDocumentAsync(fileName, storagePath, bytes.Length, ct);

            try
            {
                // 2. Raw file into Supabase Storage. We keep the original so we can
                //    re-chunk later without asking the user to upload again — worth
                //    doing, because you WILL change your chunking strategy.
                progress?.Report("Uploading to Supabase Storage...");
                await _supabase.Storage
                    .From(Bucket)
                    .Upload(bytes, storagePath, new Supabase.Storage.FileOptions
                    {
                        ContentType = Path.GetExtension(fileName).ToLowerInvariant() switch
                        {
                            ".pdf" => "application/pdf",
                            _ => "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                        },
                        Upsert = false
                    });

                // 3. Chunk.
                progress?.Report("Chunking document...");
                var _chunker = _resolver.Resolve(fileName);
                var chunks = await _chunker.ChunkAsync(buffer, fileName, new ChunkingOptions
                {
                    MaxChars = 1800,
                    OverlapChars = 200,
                    MinChars = 300,
                    SplitOnHeadings = true,
                    SplitHeadingLevel = 2
                }, ct);

                if (chunks.Count == 0)
                    throw new InvalidOperationException("No text extracted — is the document empty or image-only?");

                progress?.Report($"Embedding {chunks.Count} chunks...");
                var texts = chunks.Select(c => c.ToEmbeddingText()).ToList();
                var embeddings = await _embedder.GenerateAsync(texts, cancellationToken: ct);

                // 5. Persist. Binary COPY is dramatically faster than N inserts,
                //    and this is the bit managers notice when you demo a big file.
                progress?.Report("Saving chunks...");
                await CopyChunksAsync(documentId, chunks, embeddings, ct);

                await MarkReadyAsync(documentId, chunks.Count, ct);

                var elapsed = DateTimeOffset.UtcNow - startedAt;
                progress?.Report($"Done — {chunks.Count} chunks in {elapsed.TotalSeconds:F1}s");

                return new IngestResult(documentId, chunks.Count, elapsed, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingestion failed for {FileName}", fileName);
                await MarkFailedAsync(documentId, ex.Message, ct);
                return new IngestResult(documentId, 0, DateTimeOffset.UtcNow - startedAt, ex.Message);
            }
        }

        private async Task<Guid> InsertDocumentAsync(string fileName, string path, long size, CancellationToken ct)
        {
            const string sql = """
            insert into documents (file_name, storage_path, size_bytes, status)
            values (@name, @path, @size, 'processing')
            returning id;
            """;

            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddWithValue("name", fileName);
            cmd.Parameters.AddWithValue("path", path);
            cmd.Parameters.AddWithValue("size", size);
            return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }

        private async Task CopyChunksAsync(
            Guid documentId,
            IReadOnlyList<DocumentChunk> chunks,
            IReadOnlyList<Embedding<float>> embeddings,
            CancellationToken ct)
        {
            await using var conn = await _db.OpenConnectionAsync(ct);
            await using var writer = await conn.BeginBinaryImportAsync(
                "copy document_chunks (document_id, chunk_index, content, heading_path, page_number, embedding) from stdin (format binary)",
                ct);

            for (var i = 0; i < chunks.Count; i++)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(documentId, NpgsqlTypes.NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(chunks[i].Index, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(chunks[i].Content, NpgsqlTypes.NpgsqlDbType.Text, ct);
                await writer.WriteAsync(chunks[i].HeadingPath, NpgsqlTypes.NpgsqlDbType.Text, ct);
                if (chunks[i].PageNumber is int page)
                    await writer.WriteAsync(page, NpgsqlTypes.NpgsqlDbType.Integer, ct);
                else
                    await writer.WriteNullAsync(ct);
                await writer.WriteAsync(new Vector(embeddings[i].Vector), ct);
            }

            await writer.CompleteAsync(ct);
        }

        private async Task MarkReadyAsync(Guid id, int chunkCount, CancellationToken ct)
        {
            await using var cmd = _db.CreateCommand(
                "update documents set status = 'ready', chunk_count = @c where id = @id");
            cmd.Parameters.AddWithValue("c", chunkCount);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task MarkFailedAsync(Guid id, string error, CancellationToken ct)
        {
            await using var cmd = _db.CreateCommand(
                "update documents set status = 'failed', error_message = @e where id = @id");
            cmd.Parameters.AddWithValue("e", error.Length > 500 ? error[..500] : error);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        public async Task<List<DocumentRow>> GetDocumentsAsync(CancellationToken ct = default)
        {
            const string sql = """
            select id, file_name, size_bytes, chunk_count, status, error_message, uploaded_at
            from documents
            order by uploaded_at desc
            limit 50;
            """;

            var rows = new List<DocumentRow>();
            await using var cmd = _db.CreateCommand(sql);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                rows.Add(new DocumentRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetDateTime(6)));
            }
            return rows;
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            try
            {
                // Grab the storage path before the row disappears.
                await using var read = _db.CreateCommand("select storage_path from documents where id = @id");
                read.Parameters.AddWithValue("id", id);
                var path = (string?)await read.ExecuteScalarAsync(ct);

                await using var del = _db.CreateCommand("delete from documents where id = @id");
                del.Parameters.AddWithValue("id", id);
                await del.ExecuteNonQueryAsync(ct);   // chunks go too, via on delete cascade

                if (path is not null)
                    await _supabase.Storage.From(Bucket).Remove([path]);
            }
            catch
            {
                throw;
            }
        }

        /// <summary>Signed, time-limited download link. The bucket is private, so this is how you view a file.</summary>
        public async Task<string> GetDownloadUrlAsync(Guid id, CancellationToken ct = default)
        {
            await using var cmd = _db.CreateCommand("select storage_path from documents where id = @id");
            cmd.Parameters.AddWithValue("id", id);
            var path = (string?)await cmd.ExecuteScalarAsync(ct)
                       ?? throw new InvalidOperationException("Document not found.");

            return await _supabase.Storage.From(Bucket).CreateSignedUrl(path, 300); // 5 minutes
        }

        /// ----------------------------------------------------------------- search
        public async Task<List<SearchHit>> SearchAsync(string query, int limit = 5, CancellationToken ct = default)
        {
            var queryEmbedding = await _embedder.GenerateVectorAsync(query, cancellationToken: ct);

            // <=> is pgvector's cosine DISTANCE (0 = identical), so similarity = 1 - distance.
            const string sql = """
            select c.content, c.heading_path, d.file_name,
                   1 - (c.embedding <=> @q) as similarity, c.page_number
            from document_chunks c
            join documents d on d.id = c.document_id
            where d.status = 'ready'
            order by c.embedding <=> @q
            limit @limit;
            """;

            var hits = new List<SearchHit>();
            await using var cmd = _db.CreateCommand(sql);
            cmd.Parameters.AddWithValue("q", new Vector(queryEmbedding));
            cmd.Parameters.AddWithValue("limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                hits.Add(new SearchHit(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4)));
            }
            return hits;
        }
    }

    public record IngestResult(Guid DocumentId, int ChunkCount, TimeSpan Elapsed, string? Error)
    {
        public bool Succeeded => Error is null;
    }

    public record DocumentRow(
        Guid Id, string FileName, long SizeBytes, int ChunkCount,
        string Status, string? ErrorMessage, DateTime UploadedAt);

    public record SearchHit(string Content, string HeadingPath, string FileName, double Similarity, int? PageNumber);
}
