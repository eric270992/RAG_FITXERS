using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Embeddings;
using RAG_FITXERS.Models;
using RAG_FITXERS.Services;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace RAG_FITXERS
{
    public class RagOrchestrator
    {
        private readonly IChatCompletionService _groq;
        private readonly ITextEmbeddingGenerationService _gemini;
        private readonly DatabaseService _db;

        public RagOrchestrator(Kernel kernel, DatabaseService db)
        {
            _groq = kernel.GetRequiredService<IChatCompletionService>();
            _gemini = kernel.GetRequiredService<ITextEmbeddingGenerationService>();
            _db = db;
        }

        public async Task<string> ProcessQueryAsync(string question)
        {
            var embedding = await _gemini.GenerateEmbeddingAsync(question);
            string context = await _db.GetContextWindowAsync(embedding.ToArray());

            string answer = "";
            int attempts = 0;
            int score = 0;

            while (score < 80 && attempts < 2)
            {
                answer = await GenerateAsync(question, context);
                var judgeRes = await JudgeAsync(question, context, answer);
                score = judgeRes.Score;

                if (score < 80)
                {
                    Console.WriteLine($"[JUDGE] Score {score}: {judgeRes.Reason}. Reintentant...");
                    attempts++;
                }
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
    }
}
