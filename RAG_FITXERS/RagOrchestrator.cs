using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Embeddings;
using RAG_FITXERS.Models;
using RAG_FITXERS.Services;
using RAG_FITXERS.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace RAG_FITXERS
{
    public class RagOrchestrator
    {
        private readonly IChatCompletionService _groq;
        private readonly IEmbeddingGenerator<string, Embedding<float>> _gemini;
        private readonly DatabaseService _db;
        private readonly GraphService _graph;
        private const int minScoreThreshold = 80;

        public RagOrchestrator(Kernel kernel, DatabaseService db, GraphService graph)
        {
            _groq = kernel.GetRequiredService<IChatCompletionService>("groq");
            _gemini = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            _db = db;
            _graph = graph;
        }

        /// <summary>
        /// Pipeline RAG complet per a una pregunta de l'usuari.
        ///
        /// Implementa un sistema de recuperació jerarquitzat amb 4 nivells:
        ///   N1+2: Cerca vectorial + GraphRAG amb resums (Proposta 1)
        ///         + cerca dirigida per entitats específiques (Proposta 2)
        ///   N3:   (pendent) Resum del document
        ///   N4:   Document sencer llegit des del disc (últim recurs)
        ///
        /// El Judge avalua cada resposta i guia el sistema cap a més context
        /// si la resposta no és suficientment bona (score menor que 80).
        /// </summary>
        public async Task<string> ProcessQueryAsync(string question)
        {
            // Convertim la pregunta a un vector de 3072 dimensions (Gemini)
            // Aquest vector s'usarà per cercar chunks semànticament similars a la BD
            var embeddingResult = await _gemini.GenerateAsync(new[] { question });
            float[] embeddingVec = embeddingResult[0].Vector.ToArray();

            string answer = "";
            int attempts = 0;
            int score = 0;
            bool needsMoreContext = false;

            // Acumulem els ChunkIds vistos en tots els intents per evitar
            // repetir cerques sobre chunks que el Judge ja ha avaluat
            var allSeenChunkIds = new List<int>();

            // Bucle de reintents: cada iteració amplia el context fins que
            // el Judge dona score >= 80 o s'esgoten els 3 intents
            while (score < 80 && attempts <= 2)
            {
                // windowSize creix amb cada reintent:
                //   Intent 0 → windowSize 1 → 3 chunks  (central ± 1)
                //   Intent 1 → windowSize 2 → 5 chunks  (central ± 2)
                //   Intent 2 → windowSize 3 → 7 chunks  (central ± 3)
                int windowSize = attempts + 1;

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"\n┌─ Intent {attempts + 1} (finestra {windowSize * 2 + 1} chunks)");
                Console.ResetColor();

                // NIVELL 1 — Cerca vectorial + windowing
                // Retorna el text dels chunks més propers semànticament a la pregunta
                // i els seus IDs per poder buscar triplets associats
                var (vectorContext, chunkIds) = await _db.GetContextWindowWithIdsAsync(
                    embeddingVec, windowSize);

                // Registrem els chunks d'aquest intent per excloure'ls
                // de les cerques dirigides per entitats dels intents posteriors
                foreach (var id in chunkIds)
                    if (!allSeenChunkIds.Contains(id))
                        allSeenChunkIds.Add(id);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  [VECTOR] {chunkIds.Count} chunks: [{string.Join(", ", chunkIds)}]");
                Console.ResetColor();

                // NIVELL 2 — GraphRAG (Proposta 1)
                // Per cada chunk trobat:
                //   - Busca triplets directes a KnowledgeGraph (Subject → Predicate → Object)
                //   - Per cada entitat dels triplets, busca connexions a altres documents
                //   - Retorna els resums dels chunks creuats (~30 tokens) en lloc del text complet (~150 tokens)
                var (graphContext, _) = await _graph.GetTripletsByChunkIdsAsync(chunkIds);

                // CERCA DIRIGIDA — Judge amb entitats (Proposta 2)
                // Si el Judge de l'intent anterior ha indicat que falta informació sobre
                // entitats concretes (ex: "Carla Valls Rovira", "COMP-2024-022"),
                // les cerquem directament al KnowledgeGraph en lloc de buscar
                // totes les entitats de forma exhaustiva.
                // Això redueix soroll i tokens respecte a la cerca per connexions creuades genèrica.
                string directedContext = "";
                if (needsMoreContext && judgeResult?.MissingEntities?.Any() == true)
                {
                    // GetTripletsByEntitiesAsync busca exactament les entitats que el Judge
                    // ha indicat que falten, excloent els chunks que ja hem vist
                    directedContext = await _graph.GetTripletsByEntitiesAsync(
                        judgeResult.MissingEntities,
                        allSeenChunkIds);
                }

                // Construïm el context final combinant les tres fonts:
                //   1. vectorContext:    text dels chunks trobats per similitud semàntica
                //   2. graphContext:     triplets + resums dels documents relacionats
                //   3. directedContext:  triplets de les entitats específiques que el Judge ha demanat
                //                        (buit al primer intent, s'omple si el Judge ho indica)
                string fullContext = $"""
            CONTEXT:
            {vectorContext}

            RELACIONS:
            {(string.IsNullOrWhiteSpace(graphContext) ? "Cap." : graphContext)}
            {(string.IsNullOrWhiteSpace(directedContext) ? "" : $"\nINFORMACIÓ ADDICIONAL CERCADA:\n{directedContext}")}
            """;

                // Generem la resposta amb Groq a partir del context combinat
                answer = await GenerateAsync(question, fullContext);

                // El Judge avalua la resposta i retorna:
                //   score:           0-100 de qualitat de la resposta
                //   needsMoreContext: true si la resposta és incompleta per falta d'informació
                //   missingEntities:  entitats concretes que falten al context per respondre
                judgeResult = await JudgeAsync(question, fullContext, answer);
                score = judgeResult.Score;
                needsMoreContext = judgeResult.NeedsMoreContext;

                Console.ForegroundColor = score >= 80 ? ConsoleColor.Green : ConsoleColor.DarkRed;
                Console.WriteLine($"└─ [JUDGE] Score {score}/100 — {judgeResult.Reason}");

                // Si el Judge ha identificat entitats específiques que falten,
                // les mostrem per consola per facilitar el diagnòstic
                if (judgeResult.MissingEntities.Any())
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine($"   Entitats que falten: [{string.Join(", ", judgeResult.MissingEntities)}]");
                }
                Console.ResetColor();

                if (score < 80) attempts++;
            }

            // NIVELL 4 — Document sencer (últim recurs)
            // Si després de 3 intents amb GraphRAG el Judge no ha aprovat la resposta,
            // llegim el document sencer des del disc i generem la resposta sense Judge.
            // Nota: aquest nivell NO passa pel Judge perquè és el màxim context possible.
            if (score < 80)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine("\n[N4] Cap intent ha superat el llindar — llegint document sencer...");
                Console.ResetColor();

                string fullDocument = await GetFullDocumentAsync(question);
                return await GenerateAsync(question, fullDocument);
            }

            return answer;
        }

        // Guardem el resultat del Judge entre iteracions del bucle
        // per poder accedir a MissingEntities al proper intent
        private JudgeResult? judgeResult;

        private async Task<string> GenerateAsync(string q, string c)
        {
            var prompt = $"Ets un assistent. Basant-te en aquest context: {c}\nRespon: {q}";
            return (await _groq.GetChatMessageContentAsync(prompt)).ToString();
        }

        private async Task<JudgeResult> JudgeAsync(string q, string c, string a)
        {
            var prompt = $@"Avalua la resposta (0-100) segons el context proporcionat.
        Respon NOMÉS en JSON sense cap text addicional:
        {{
            ""score"": 80,
            ""reason"": ""motiu breu"",
            ""needsMoreContext"": true,
            ""missingEntities"": [""entitat1"", ""entitat2""]
        }}

        REGLES:
        - score: 0-100 segons si la resposta és correcta i completa
        - needsMoreContext: true si la resposta és incompleta per FALTA d'informació
                           false si el context és suficient però la resposta és dolenta
        - missingEntities: llista de noms concrets que FALTEN al context per respondre
                           ex: [""Carla Valls Rovira"", ""COMP-2024-022""]
                           buit [] si no falta cap entitat específica

        CONTEXT: {c}
        PREGUNTA: {q}
        RESPOSTA: {a}";

            var raw = (await _groq.GetChatMessageContentAsync(prompt)).ToString();

            try
            {
                var cleanJson = raw.Substring(raw.IndexOf("{"));
                return JsonSerializer.Deserialize<JudgeResult>(cleanJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            }
            catch
            {
                // Si el Judge falla el parse, retornem score baix per forçar reintent
                return new JudgeResult { Score = 0, Reason = "Error parse Judge", NeedsMoreContext = true };
            }
        }

        /// <summary>
        /// Nivell 4 del Hierarchical Retrieval — Últim recurs.
        /// Llegeix el document sencer des del disc quan cap altra cerca
        /// ha trobat prou informació per respondre la pregunta.
        ///
        /// Flux:
        ///   Pregunta → Embedding → Ruta del fitxer (BD) → Text sencer (disc)
        /// </summary>
        public async Task<string> GetFullDocumentAsync(string question)
        {
            // Convertim la pregunta a un vector de 3072 dimensions (gemini-embedding-001)
            // per poder buscar quin document és el més rellevant semànticament
            var result = await _gemini.GenerateAsync(new[] { question });
            float[] vec = result[0].Vector.ToArray();

            // Busquem a PostgreSQL la ruta del document el chunk del qual
            // té el vector més proper al de la pregunta (distància cosinus)
            // Retorna el FilePath del document més rellevant
            string filePath = await _db.GetMostRelevantFilePathAsync(vec);

            // Verifiquem que la ruta existeix tant a la BD com al disc
            // És possible que el fitxer s'hagi eliminat o mogut des de la ingestió
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return "No s'ha trobat el document al disc.";

            Console.WriteLine($"[N4] Llegint document sencer: {filePath}");

            // Llegim el contingut sencer del fitxer des del disc.
            // FileUtils.ExtractAsync suporta múltiples formats (PDF, DOCX, TXT...)
            // i retorna el text net sense format, llest per enviar al LLM.
            // NOTA: Aquest text pot ser molt llarg — és tot el document sense chunketjar.
            return await FileUtils.ExtractAsync(filePath);
        }



    }
}
