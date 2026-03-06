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

        public RagOrchestrator(Kernel kernel, DatabaseService db, GraphService graph)
        {
            _groq = kernel.GetRequiredService<IChatCompletionService>("groq");
            _gemini = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
            _db = db;
            _graph = graph;
        }

        public async Task<string> ProcessQueryAsync(string question)
        {
            var embeddingResult = await _gemini.GenerateAsync(new[] { question });
            float[] embeddingVec = embeddingResult[0].Vector.ToArray();

            string answer = "";
            int attempts = 0;
            int score = 0;

            // Cada reintent amplia la finestra de context:
            // Intent 0 → windowSize 1 → 3 chunks
            // Intent 1 → windowSize 2 → 5 chunks
            // Intent 2 → windowSize 3 → 7 chunks
            while (score < 80 && attempts <= 2)
            {
                int windowSize = attempts + 1;

                // 1. Context vectorial + IDs dels chunks trobats
                var (vectorContext, chunkIds) = await _db.GetContextWindowWithIdsAsync(
                    embeddingVec, windowSize);

                // 2. Triplets + IDs dels chunks de les connexions creuades
                var (graphContext, crossChunkIds) = await _graph.GetTripletsByChunkIdsAsync(chunkIds);

                // 3. Text dels chunks de les connexions creuades
                string crossContext = await _db.GetChunksByIdsAsync(crossChunkIds);

                // 4. Context final combinat
                // Enviarem al LLM el context vectorial dels chunks principals, el text dels chunks relacionats (si n'hi ha)
                string fullContext = $"""
                    CONTEXT DOCUMENTAL (chunks principals):
                    {vectorContext}

                    CONTEXT DOCUMENTAL (chunks relacionats):
                    {(string.IsNullOrWhiteSpace(crossContext) ? "Cap chunk relacionat trobat." : crossContext)}

                    RELACIONS CONEGUDES:
                    {(string.IsNullOrWhiteSpace(graphContext) ? "Cap relació trobada." : graphContext)}
                    """;

                Console.WriteLine($"[RAG] Intent {attempts + 1} — finestra {windowSize * 2 + 1} chunks " +
                                  $"+ {crossChunkIds.Count} chunks creuats...");

                answer = await GenerateAsync(question, fullContext);
                var judgeRes = await JudgeAsync(question, fullContext, answer);
                score = judgeRes.Score;

                if (score < 80)
                {
                    Console.WriteLine($"[JUDGE] Score {score}/100: {judgeRes.Reason}. Ampliant context...");
                    attempts++;
                }
            }

            //Si la cerca vectorial + graf no és suficient, oferim el document sencer:
            if (score < 80)
            {
                // 1. Recuperem el text sencer del document
                string fullDocument = await GetFullDocumentAsync(question);

                // 2. Generem la resposta amb Groq a partir del document sencer
                return await GenerateAsync(question, fullDocument);
            }

            return answer;
        }

        private async Task<string> GenerateAsync(string q, string c)
        {
            var prompt = $"Ets un assistent. Basant-te en aquest context: {c}\nRespon: {q}";
            return (await _groq.GetChatMessageContentAsync(prompt)).ToString();
        }

        private async Task<JudgeResult> JudgeAsync(string q, string c, string a)
        {
            var prompt = $@"Avalua la resposta (0-100) segons el context. Respon en JSON: {{""score"": 80, ""reason"": ""...""}}
                        CONTEXT: {c} | PREGUNTA: {q} | RESPOSTA: {a}";

            var raw = (await _groq.GetChatMessageContentAsync(prompt)).ToString();
            var cleanJson = raw.Substring(raw.IndexOf("{")); // Per si l'IA xerra de més
            return JsonSerializer.Deserialize<JudgeResult>(cleanJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
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
