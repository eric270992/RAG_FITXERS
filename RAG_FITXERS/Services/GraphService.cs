using DocumentFormat.OpenXml.InkML;
using DocumentFormat.OpenXml.Office2010.Excel;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Npgsql;
using RAG_FITXERS.Models;
using System.Runtime.ConstrainedExecution;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RAG_FITXERS.Services
{
    public class GraphService
    {
        private readonly IChatCompletionService _llm;
        private readonly NpgsqlDataSource _dataSource;
        private const int MIN_CONFIDENCE = 80;

        public GraphService(Kernel kernel, NpgsqlDataSource dataSource)
        {
            _llm = kernel.GetRequiredService<IChatCompletionService>("groq");
            _dataSource = dataSource;
        }

        /// <summary>
        /// Agent extractor de triplets. Per cada chunk decideix:
        ///   - "triplets" → ha trobat relacions fiables (confidence >= 80)
        ///   - "discard"  → no hi ha relacions clares o el chunk no té informació útil
        ///
        /// Exemples de triplets acceptats (confidence >= 80):
        ///   Joan García (Director) → director de → Logística
        ///   Logística              → ha comprat  → Furgonetes elèctriques
        ///
        /// Exemples de triplets descartats (confidence < 80):
        ///   algú    → gestiona    → alguna cosa  ← massa vague
        ///   empresa → té          → empleats     ← massa genèric
        /// </summary>
        public async Task ProcessChunkAsync(int docId, int chunkId, string chunkText)
        {
            string prompt = $@"Ets un agent extractor de coneixement.
                Analitza el text i extreu TOTES les relacions importants que hi trobes.
                Respon NOMÉS en JSON amb aquest format exacte:

                {{
                  ""mode"": ""triplets"" | ""discard"",
                  ""relations"": [
                    {{
                      ""subject"": ""entitat o concepte principal"",
                      ""predicate"": ""tipus de relació o propietat"",
                      ""object"": ""entitat, valor o concepte relacionat"",
                      ""confidence"": 0-100
                    }}
                  ]
                }}

                REGLES GENERALS:
                - Extreu QUALSEVOL relació verificable al text:
                  * Qui fa què
                  * Qui té quina propietat (càrrec, data, valor, import...)
                  * Què pertany a què
                  * Què és responsable de què
                  * Quina xifra o data s'associa a quin concepte
                - Usa sempre el nom o identificador MÉS COMPLET possible
                  Correcte:   ""Elena Puig Gual""
                  Incorrecte: ""Elena""
                - Si una entitat té context important, afegeix-lo entre parèntesis
                  Exemple: ""Jordi Amat Bosc (Director Financer)""
                - mode='triplets' si hi ha relacions clares i verificables
                - mode='discard' si el text és descriptiu sense dades concretes,
                  un índex, una capçalera o no té informació factual útil
                - confidence < {MIN_CONFIDENCE} → no incloguis el triplet

                TEXT:
                {chunkText}";

            try
            {
                var raw = (await _llm.GetChatMessageContentAsync(prompt)).ToString();

                int start = raw.IndexOf("{");
                int end = raw.LastIndexOf("}") + 1;
                if (start == -1 || end == 0)
                {
                    Console.WriteLine($"[GRAPH] Chunk {chunkId}: resposta invàlida, descartant.");
                    return;
                }

                var result = JsonSerializer.Deserialize<ExtractionResult>(
                    raw[start..end],
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (result == null) return;

                switch (result.Mode.ToLower())
                {
                    case "triplets":
                        await SaveTripletsAsync(docId, chunkId, result);
                        break;

                    case "discard":
                        Console.WriteLine($"[GRAPH] Chunk {chunkId}: descartat.");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GRAPH] Error al chunk {chunkId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Desa els triplets que superen el llindar de confiança.
        /// Els triplets per sota de MIN_CONFIDENCE es descarten silenciosament.
        /// </summary>
        private async Task SaveTripletsAsync(int docId, int chunkId, ExtractionResult result)
        {
            var fiables = result.Relations
                .Where(r => r.Confidence >= MIN_CONFIDENCE
                    && !string.IsNullOrWhiteSpace(r.Subject)
                    && !string.IsNullOrWhiteSpace(r.Predicate)
                    && !string.IsNullOrWhiteSpace(r.Object))
                .ToList();

            if (fiables.Count == 0)
            {
                Console.WriteLine($"[GRAPH] Chunk {chunkId}: cap triplet fiable, descartant.");
                return;
            }

            using var conn = await _dataSource.OpenConnectionAsync();
            foreach (var rel in fiables)
            {
                using var cmd = new NpgsqlCommand(@"
                    INSERT INTO KnowledgeGraph
                        (DocumentId, ChunkId, Subject, Predicate, Object)
                    VALUES
                        (@docId, @chunkId, @s, @p, @o)
                    ON CONFLICT DO NOTHING", conn);

                cmd.Parameters.AddWithValue("docId", docId);
                cmd.Parameters.AddWithValue("chunkId", chunkId);
                cmd.Parameters.AddWithValue("s", rel.Subject.Trim());
                cmd.Parameters.AddWithValue("p", rel.Predicate.Trim());
                cmd.Parameters.AddWithValue("o", rel.Object.Trim());
                await cmd.ExecuteNonQueryAsync();
            }

            Console.WriteLine($"[GRAPH] Chunk {chunkId}: {fiables.Count} triplets guardats " +
                              $"({result.Relations.Count - fiables.Count} descartats per baixa confiança).");
        }

        /// <summary>
        /// Cerca relacions al graf per una entitat concreta.
        /// Retorna les relacions formatades incloent el document origen via JOIN.
        /// </summary>
        public async Task<string> GetRelationsForEntityAsync(string entity)
        {
            using var conn = await _dataSource.OpenConnectionAsync();
            using var cmd = new NpgsqlCommand(@"
                SELECT d.FileName, kg.Subject, kg.Predicate, kg.Object
                FROM KnowledgeGraph kg
                INNER JOIN Documents d ON d.Id = kg.DocumentId
                WHERE lower(kg.Subject) LIKE lower(@e)
                   OR lower(kg.Object)  LIKE lower(@e)
                ORDER BY d.FileName", conn);

            cmd.Parameters.AddWithValue("e", $"%{entity.Trim()}%");

            var sb = new StringBuilder();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                sb.AppendLine($"[{r.GetString(0)}] {r.GetString(1)} → {r.GetString(2)} → {r.GetString(3)}");

            return sb.ToString();
        }

        /// <summary>
        /// Construeix el context del graf de coneixement per a una pregunta.
        ///
        /// Funciona en dos passos:
        ///   PAS 1 — Extracció d'entitats:
        ///     Demana al LLM que identifiqui les entitats principals de la pregunta.
        ///     Exemple: "Què ha comprat en Joan García?"
        ///     → Entitats: ["Joan García"]
        ///
        ///   PAS 2 — Cerca de relacions:
        ///     Per cada entitat, busca a KnowledgeGraph totes les relacions
        ///     on apareix com a Subject o Object.
        ///     → [rrhh.pdf] Joan García → director de → Logística
        ///     → [compres.pdf] Logística → ha comprat → Furgonetes
        ///
        /// El resultat s'injecta al prompt del LLM perquè pugui connectar
        /// informació entre documents que la cerca vectorial no connectaria.
        /// </summary>
        public async Task<string> GetGraphContextAsync(string question)
        {
            // PAS 1: Demanem al LLM les entitats de la pregunta
            string entityPrompt = $@"Identifica les entitats principals 
            (noms de persones, departaments, productes, llocs) d'aquesta pregunta.
            Retorna NOMÉS una llista separada per comes, sense explicacions ni text addicional.
            Pregunta: {question}";

            var entitiesRaw = (await _llm.GetChatMessageContentAsync(entityPrompt)).ToString();
            var entities = entitiesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToList();

            if (entities.Count == 0) return "";

            // PAS 2: Per cada entitat, busquem les seves relacions al graf
            var sb = new StringBuilder();
            foreach (var entity in entities)
            {
                string relations = await GetRelationsForEntityAsync(entity);
                if (!string.IsNullOrWhiteSpace(relations))
                    sb.AppendLine(relations);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Mètode que retorna els triplets associats a una llista de ChunkIds, incloent també les connexions creuades amb altres chunks que comparteixen entitats.
        /// </summary>
        /// <param name="chunkIds"></param>
        /// <returns>
        /// Cadena formatada amb els triplets associats als ChunkIds proporcionats, incloent les connexions creuades.
        /// </returns>
        public async Task<(string Triplets, List<int> CrossChunkIds)> GetTripletsByChunkIdsAsync(List<int> chunkIds)
        {
            if (chunkIds.Count == 0) return ("", new List<int>());

            using var conn = await _dataSource.OpenConnectionAsync();
            var idList = string.Join(",", chunkIds);
            var sb = new StringBuilder();
            var entities = new HashSet<string>();
            var crossChunkIds = new List<int>();  // ← NOU: guardem els ChunkIds creuats

            // PAS 3: Triplets directes
            using (var cmd = new NpgsqlCommand($@"
                SELECT d.FileName, kg.Subject, kg.Predicate, kg.Object
                FROM KnowledgeGraph kg
                INNER JOIN Documents d ON d.Id = kg.DocumentId
                WHERE kg.ChunkId IN ({idList})", conn))
            {
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    sb.AppendLine($"[{r.GetString(0)}] {r.GetString(1)} → {r.GetString(2)} → {r.GetString(3)}");
                    entities.Add(r.GetString(1));
                    entities.Add(r.GetString(3));
                }
            }

            // PAS 4: Connexions creuades — ara també recollim els ChunkIds
            foreach (var entity in entities)
            {
                using var cmd = new NpgsqlCommand($@"
                    SELECT kg.ChunkId, d.FileName, kg.Subject, kg.Predicate, kg.Object
                    FROM KnowledgeGraph kg
                    INNER JOIN Documents d ON d.Id = kg.DocumentId
                    WHERE (lower(kg.Subject) LIKE lower(@e)
                       OR  lower(kg.Object)  LIKE lower(@e))
                    AND kg.ChunkId NOT IN ({idList})", conn);

                cmd.Parameters.AddWithValue("e", $"%{entity}%");

                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    int crossChunkId = r.GetInt32(0);
                    sb.AppendLine($"[CONNEXIÓ][{r.GetString(1)}] {r.GetString(2)} → {r.GetString(3)} → {r.GetString(4)}");

                    // Guardem el ChunkId del document connectat
                    if (!crossChunkIds.Contains(crossChunkId))
                        crossChunkIds.Add(crossChunkId);  // ← NOU
                }
            }

            return (sb.ToString(), crossChunkIds);  // ← retornem també els ChunkIds creuats
        }

    }
}