using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Npgsql;
using RAG_FITXERS.Models;
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
            string prompt = $@"Ets un agent extractor de coneixement empresarial.
Analitza el text i respon NOMÉS en JSON amb aquest format exacte:

{{
  ""mode"": ""triplets"" | ""discard"",
  ""relations"": [
    {{
      ""subject"": ""nom complet de l'entitat A"",
      ""predicate"": ""tipus de relació"",
      ""object"": ""nom complet de l'entitat B"",
      ""confidence"": 0-100
    }}
  ]
}}

REGLES:
- Usa SEMPRE noms complets per evitar ambigüitats (ex: 'Joan García', mai 'Joan')
- Si dues entitats tenen el mateix nom afegeix context (ex: 'Maria López (RRHH)')
- mode='triplets' NOMÉS si les relacions són clares i verificables al text
- mode='discard' si el text és descriptiu, un índex, capçalera o sense entitats clares
- Un triplet amb confidence < {MIN_CONFIDENCE} és millor no incloure'l
- Si no n'hi ha cap de fiable, usa mode='discard'

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
    }
}