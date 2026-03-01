using System;
using System.Collections.Generic;
using System.Text;
using Npgsql;

namespace RAG_FITXERS.Services
{
    public class DatabaseService
    {
        private readonly string _connStr;
        public DatabaseService(string connStr) => _connStr = connStr;

        public async Task<int?> GetIdByHashAsync(string hash)
        {
            using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand("SELECT Id FROM Documents WHERE FileHash = @h", conn);
            cmd.Parameters.AddWithValue("h", hash);
            var res = await cmd.ExecuteScalarAsync();
            return res != null ? (int)res : null;
        }

        public async Task<int> RegisterDocumentAsync(string name, string path, string hash)
        {
            using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync();
            // Neteja si el fitxer ja existia amb un altre hash
            using (var del = new NpgsqlCommand("DELETE FROM Documents WHERE FileName = @n", conn))
            {
                del.Parameters.AddWithValue("n", name);
                await del.ExecuteNonQueryAsync();
            }
            using var cmd = new NpgsqlCommand("INSERT INTO Documents (FileName, FilePath, FileHash) VALUES (@n, @p, @h) RETURNING Id", conn);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("p", path);
            cmd.Parameters.AddWithValue("h", hash);
            return (int)await cmd.ExecuteScalarAsync();
        }

        public async Task SaveChunkAsync(int docId, int idx, string content, float[] vector)
        {
            using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand("INSERT INTO DocumentChunks (DocumentId, ChunkIndex, RawContent, Embedding) VALUES (@d, @i, @c, @v)", conn);
            cmd.Parameters.AddWithValue("d", docId);
            cmd.Parameters.AddWithValue("i", idx);
            cmd.Parameters.AddWithValue("c", content);
            cmd.Parameters.AddWithValue("v", vector);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<string> GetContextWindowAsync(float[] queryVector)
        {
            using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync();

            // 1. Cerca el millor fragment
            int docId, centerIdx;
            using (var cmd = new NpgsqlCommand("SELECT DocumentId, ChunkIndex FROM DocumentChunks ORDER BY Embedding <=> @v LIMIT 1", conn))
            {
                cmd.Parameters.AddWithValue("v", queryVector);
                using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return "";
                docId = r.GetInt32(0); centerIdx = r.GetInt32(1);
            }

            // 2. Windowing (N-1, N, N+1)
            var sb = new StringBuilder();
            using (var cmd = new NpgsqlCommand("SELECT RawContent FROM DocumentChunks WHERE DocumentId = @d AND ChunkIndex BETWEEN @min AND @max ORDER BY ChunkIndex", conn))
            {
                cmd.Parameters.AddWithValue("d", docId);
                cmd.Parameters.AddWithValue("min", centerIdx - 1);
                cmd.Parameters.AddWithValue("max", centerIdx + 1);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) sb.AppendLine(r.GetString(0));
            }
            return sb.ToString();
        }
    }
}
