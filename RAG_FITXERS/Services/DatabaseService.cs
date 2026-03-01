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

        /// <summary>
        /// Inicialitza el servei configurant el suport per a vectors.
        /// UseVector() registra els tipus de pgvector (Vector) perquè Npgsql
        /// pugui serialitzar/deserialitzar vectors entre C# i PostgreSQL.
        /// </summary>
        public DatabaseService(string connStr)
        {
            var builder = new NpgsqlDataSourceBuilder(connStr);
            builder.UseVector(); // Habilita la conversió automàtica entre float[] i el tipus VECTOR de pgvector
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
        /// Registra un nou document a la base de dades.
        /// 
        /// Primer elimina qualsevol versió anterior del mateix fitxer (per nom) per evitar
        /// duplicats en cas de re-ingestió d'un document actualitzat. Després insereix
        /// el nou registre i retorna l'ID generat, que s'usarà per associar els chunks.
        /// </summary>
        /// <returns>L'ID autogenerat del document inserit.</returns>
        public async Task<int> RegisterDocumentAsync(string name, string path, string hash)
        {
            using var conn = await _dataSource.OpenConnectionAsync();

            // Elimina la versió anterior del document si existia (re-ingestió)
            using (var del = new NpgsqlCommand("DELETE FROM Documents WHERE FileName = @n", conn))
            {
                del.Parameters.AddWithValue("n", name);
                await del.ExecuteNonQueryAsync();
            }

            // RETURNING Id retorna directament l'ID generat per SERIAL sense fer un SELECT addicional
            using var cmd = new NpgsqlCommand(
                "INSERT INTO Documents (FileName, FilePath, FileHash) VALUES (@n, @p, @h) RETURNING Id", conn);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.AddWithValue("p", path);
            cmd.Parameters.AddWithValue("h", hash);
            return (int)await cmd.ExecuteScalarAsync();
        }

        /// <summary>
        /// Desa un fragment (chunk) de document juntament amb el seu embedding vectorial.
        /// 
        /// QUÈ ÉS UN CHUNK?
        /// Els documents es divideixen en fragments petits (chunks) perquè:
        ///   - Els LLMs tenen una finestra de context limitada
        ///   - La cerca vectorial funciona millor amb fragments petits i concrets
        ///   - Permet recuperar només la part rellevant, no tot el document
        /// 
        /// QUÈ ÉS UN EMBEDDING?
        /// Un embedding és una representació numèrica del significat semàntic d'un text,
        /// expressada com un vector de N dimensions (en aquest cas 3072 amb gemini-embedding-001).
        /// Texts amb significat similar tindran vectors similars (pròxims en l'espai vectorial).
        /// 
        /// Exemple visual (simplificat a 2D):
        ///   "El gat menja"     → [0.9, 0.1]
        ///   "El felí s'alimenta" → [0.85, 0.12]  ← molt pròxim, semànticament similar
        ///   "La borsa puja"    → [0.1, 0.95]     ← llunyà, semànticament diferent
        /// </summary>
        public async Task SaveChunkAsync(int docId, int index, string content, float[] vec)
        {
            using var conn = await _dataSource.OpenConnectionAsync();

            // Convertim float[] a Vector, el tipus que entén pgvector
            var vector = new Pgvector.Vector(vec);

            using var cmd = new NpgsqlCommand(
                "INSERT INTO DocumentChunks (DocumentId, ChunkIndex, RawContent, Embedding) VALUES (@d, @i, @c, @v)", conn);
            cmd.Parameters.AddWithValue("d", docId);
            cmd.Parameters.AddWithValue("i", index);
            cmd.Parameters.AddWithValue("c", content); // Text original llegible per humans
            cmd.Parameters.AddWithValue("v", vector);  // Vector per a cerca semàntica
            await cmd.ExecuteNonQueryAsync();
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
        public async Task<string> GetContextWindowAsync(float[] queryVector)
        {
            using var conn = await _dataSource.OpenConnectionAsync();
            var vector = new Pgvector.Vector(queryVector);

            // PAS 1: Cerca vectorial — troba el chunk semànticament més proper a la pregunta
            // L'operador <=> és la distància cosinus, proporcionada per pgvector
            int docId, centerIdx;
            using (var cmd = new NpgsqlCommand(
                "SELECT DocumentId, ChunkIndex FROM DocumentChunks ORDER BY Embedding <=> @v LIMIT 1", conn))
            {
                cmd.Parameters.AddWithValue("v", vector);
                using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return ""; // Cap document ingerit encara
                docId = r.GetInt32(0);
                centerIdx = r.GetInt32(1); // Índex del chunk central trobat
            }

            // PAS 2: Windowing — recupera el chunk trobat i els seus veïns immediats
            // BETWEEN @min AND @max selecciona: chunk anterior, central i posterior
            var sb = new StringBuilder();
            using (var cmd = new NpgsqlCommand(
                @"SELECT RawContent FROM DocumentChunks 
                  WHERE DocumentId = @d AND ChunkIndex BETWEEN @min AND @max 
                  ORDER BY ChunkIndex", conn))
            {
                cmd.Parameters.AddWithValue("d", docId);
                cmd.Parameters.AddWithValue("min", centerIdx - 1); // Chunk anterior
                cmd.Parameters.AddWithValue("max", centerIdx + 1); // Chunk posterior
                using var r = await cmd.ExecuteReaderAsync();

                // Concatenem els chunks en ordre per formar un context coherent
                while (await r.ReadAsync()) sb.AppendLine(r.GetString(0));
            }

            return sb.ToString(); // Aquest text s'injectarà al prompt del LLM com a context
        }
    }
}