using Npgsql;
using Pgvector;
using System.Text;

namespace RAG_FITXERS.Services
{
    /// <summary>
    /// Servei d'accés a dades per a un sistema RAG (Retrieval-Augmented Generation).
    /// 
    /// RAG és una tècnica que combina:
    ///   1. Una base de dades vectorial (PostgreSQL + pgvector) per emmagatzemar i cercar fragments de documents
    ///   2. Un model de llenguatge (LLM) que genera respostes basades en el context recuperat
    /// 
    /// Flux general:
    ///   INGESTIÓ:  Document → Text → Chunks → Embeddings → PostgreSQL
    ///   CONSULTA:  Pregunta → Embedding → Cerca vectorial → Context → LLM → Resposta
    /// </summary>
    public class DatabaseService
    {
        /// <summary>
        /// Font de connexions a PostgreSQL configurada amb suport per a pgvector.
        /// pgvector és una extensió de PostgreSQL que permet emmagatzemar vectors de N dimensions
        /// i fer cerques per similitud semàntica directament a la base de dades.
        /// </summary>
        private readonly NpgsqlDataSource _dataSource;

        // Exposem el DataSource per poder-lo reutilitzar a altres serveis (ex: GraphService)
        public NpgsqlDataSource DataSource => _dataSource;


        /// <summary>
        /// Inicialitza el servei configurant el suport per a vectors.
        /// UseVector() registra els tipus de pgvector (Vector) perquè Npgsql
        /// pugui serialitzar/deserialitzar vectors entre C# i PostgreSQL.
        /// </summary>
        public DatabaseService(string connStr)
        {
            var builder = new NpgsqlDataSourceBuilder(connStr);
            builder.UseVector();
            _dataSource = builder.Build();
        }

        /// <summary>
        /// Comprova si un document ja ha estat processat prèviament mitjançant el seu hash.
        /// 
        /// El hash (MD5/SHA) és una empremta digital única del fitxer. Si el hash ja existeix
        /// a la BD, vol dir que el document ja s'ha ingerit i no cal tornar-lo a processar,
        /// evitant duplicats i feina innecessària.
        /// </summary>
        /// <returns>L'ID del document si existeix, null si és nou.</returns>
        public async Task<int?> GetIdByHashAsync(string hash)
        {
            using var conn = await _dataSource.OpenConnectionAsync();
            using var cmd = new NpgsqlCommand("SELECT Id FROM Documents WHERE FileHash = @h", conn);
            cmd.Parameters.AddWithValue("h", hash);
            var res = await cmd.ExecuteScalarAsync();
            return res != null ? (int)res : null;
        }

        /// <summary>
        /// Registra un document i tots els seus chunks de forma atòmica dins d'una transacció.
        /// 
        /// PER QUÈ UNA TRANSACCIÓ?
        /// La ingestió d'un document és un procés de dos passos:
        ///   1. Inserir el document a la taula Documents
        ///   2. Inserir N chunks amb els seus embeddings a DocumentChunks
        /// 
        /// Si el procés falla a meitat (per exemple, error de xarxa cridant Gemini al chunk 5 de 20),
        /// sense transacció quedaríem amb un document registrat però amb chunks incomplets,
        /// cosa que donaria resultats incorrectes en les cerques vectorials.
        /// 
        /// Amb la transacció, si qualsevol pas falla → ROLLBACK complet → com si no hagués passat res.
        /// El document es podrà tornar a processar íntegrament en la propera execució.
        /// 
        /// FLUX:
        ///   BEGIN
        ///     DELETE document anterior (si existia)
        ///     INSERT document → obté docId
        ///     INSERT chunk 0 amb embedding
        ///     INSERT chunk 1 amb embedding
        ///     ...
        ///     INSERT chunk N amb embedding
        ///   COMMIT  ← només si tot ha anat bé
        ///   ROLLBACK ← si qualsevol pas falla
        /// </summary>
        public async Task<int?> RegisterDocumentWithChunksAsync(
            string name, string path, string hash, List<(string Content, float[] Embedding)> chunks)
        {
            using var conn = await _dataSource.OpenConnectionAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // PAS 1: Elimina versió anterior del document (re-ingestió)
                using (var del = new NpgsqlCommand("DELETE FROM Documents WHERE FileName = @n", conn, tx))
                {
                    del.Parameters.AddWithValue("n", name);
                    await del.ExecuteNonQueryAsync();
                }

                // PAS 2: Insereix el document i obté l'ID generat
                int docId;
                using (var cmd = new NpgsqlCommand(
                    "INSERT INTO Documents (FileName, FilePath, FileHash) VALUES (@n, @p, @h) RETURNING Id", conn, tx))
                {
                    cmd.Parameters.AddWithValue("n", name);
                    cmd.Parameters.AddWithValue("p", path);
                    cmd.Parameters.AddWithValue("h", hash);
                    docId = (int)await cmd.ExecuteScalarAsync();
                }

                // PAS 3: Insereix tots els chunks amb els seus embeddings
                for (int i = 0; i < chunks.Count; i++)
                {
                    var (content, vec) = chunks[i];
                    var vector = new Pgvector.Vector(vec);

                    using var cmd = new NpgsqlCommand(
                        "INSERT INTO DocumentChunks (DocumentId, ChunkIndex, RawContent, Embedding) VALUES (@d, @i, @c, @v)",
                        conn, tx);
                    cmd.Parameters.AddWithValue("d", docId);
                    cmd.Parameters.AddWithValue("i", i);
                    cmd.Parameters.AddWithValue("c", content);
                    cmd.Parameters.AddWithValue("v", vector);
                    await cmd.ExecuteNonQueryAsync();
                }

                // Tot ha anat bé → confirmem els canvis
                await tx.CommitAsync();
                Console.WriteLine($"[DB] Document '{name}' guardat correctament ({chunks.Count} chunks).");
                return docId;
            }
            catch (Exception ex)
            {
                // Qualsevol error → desfem tots els canvis, com si no hagués passat res
                await tx.RollbackAsync();
                Console.WriteLine($"[ERROR] No s'ha pogut guardar '{name}': {ex.Message}");
                Console.WriteLine($"        El document es tornarà a processar en la propera execució.");
                return null;
            }
        }

        /// <summary>
        /// Recupera el context rellevant per respondre una pregunta. Aquest és el cor del RAG.
        /// 
        /// EL PROCÉS EN DETALL:
        /// 
        /// PAS 1 - CERCA VECTORIAL:
        ///   La pregunta s'ha convertit prèviament en un embedding (vector).
        ///   Busquem el chunk de la BD el vector del qual és més similar al de la pregunta.
        ///   Usem la distància cosinus (<=>), que mesura l'angle entre vectors:
        ///     - Distància 0   = vectors idèntics (màxima similitud)
        ///     - Distància 1   = vectors perpendiculars (cap relació)
        ///     - Distància 2   = vectors oposats
        ///   ORDER BY Embedding <=> @v retorna el chunk més semànticament proper a la pregunta.
        /// 
        /// PAS 2 - WINDOWING (FINESTRA DE CONTEXT):
        ///   No retornem només el chunk trobat, sinó també els seus veïns (N-1, N, N+1).
        ///   Això és important perquè la informació rellevant sovint es troba repartida
        ///   entre chunks consecutius (per exemple, una pregunta i la seva resposta).
        ///   Retornar el context complet millora molt la qualitat de la resposta del LLM.
        /// 
        /// EXEMPLE:
        ///   Chunk 4: "...el tractament consisteix en..."   ← veí anterior
        ///   Chunk 5: "...dosi recomanada és 500mg..."      ← chunk trobat (més similar)
        ///   Chunk 6: "...efectes secundaris inclouen..."   ← veí posterior
        ///   → Retornem els 3 chunks junts com a context per al LLM
        /// </summary>
        /// <param name="queryVector">L'embedding de la pregunta de l'usuari (3072 dimensions).</param>
        /// <returns>Text concatenat dels chunks més rellevants per usar com a context del LLM.</returns>
        /// <param name="windowSize">
        /// Nombre de chunks veïns a cada costat del chunk central.
        /// windowSize=1 → 3 chunks (N-1, N, N+1)
        /// windowSize=2 → 5 chunks (N-2, N-1, N, N+1, N+2)
        /// windowSize=3 → 7 chunks (N-3...N+3)
        /// </param>
        public async Task<string> GetContextWindowAsync(float[] queryVector, int windowSize = 1)
        {
            using var conn = await _dataSource.OpenConnectionAsync();
            var vector = new Pgvector.Vector(queryVector);

            int docId, centerIdx;
            using (var cmd = new NpgsqlCommand(
                "SELECT DocumentId, ChunkIndex FROM DocumentChunks ORDER BY Embedding <=> @v LIMIT 1", conn))
            {
                cmd.Parameters.AddWithValue("v", vector);
                using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return "";
                docId = r.GetInt32(0);
                centerIdx = r.GetInt32(1);
            }

            var sb = new StringBuilder();
            using (var cmd = new NpgsqlCommand(
                @"SELECT RawContent FROM DocumentChunks 
          WHERE DocumentId = @d AND ChunkIndex BETWEEN @min AND @max 
          ORDER BY ChunkIndex", conn))
            {
                cmd.Parameters.AddWithValue("d", docId);
                cmd.Parameters.AddWithValue("min", centerIdx - windowSize);
                cmd.Parameters.AddWithValue("max", centerIdx + windowSize);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) sb.AppendLine(r.GetString(0));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Retorna la ruta del fitxer del document més rellevant per a la pregunta.
        /// S'usa al Nivell 4 per llegir el document sencer des del disc
        /// en lloc de tenir el contingut duplicat a la BD.
        /// </summary>
        public async Task<string> GetMostRelevantFilePathAsync(float[] queryVector)
        {
            using var conn = await _dataSource.OpenConnectionAsync();
            var vector = new Pgvector.Vector(queryVector);

            using var cmd = new NpgsqlCommand(@"
                SELECT d.FilePath
                FROM DocumentChunks dc
                INNER JOIN Documents d ON d.Id = dc.DocumentId
                ORDER BY dc.Embedding <=> @v
                LIMIT 1", conn);

            cmd.Parameters.AddWithValue("v", vector);
            var result = await cmd.ExecuteScalarAsync();
            return result?.ToString() ?? "";
        }
    }
}