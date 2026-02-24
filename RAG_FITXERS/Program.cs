using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.SemanticKernel.Text; // Important per TextChunker
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig; // Cal afegir aquest!
using System.Linq;

namespace RAG_FITXERS
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // --- 1. CONFIGURACIÓ ---
            var builder = Kernel.CreateBuilder();

            // Configurar Groq
            builder.AddOpenAIChatCompletion(
                modelId: "llama3-8b-8192",
                apiKey: "LA_TEVA_GROQ_API_KEY",
                endpoint: new Uri("https://api.groq.com/openai/v1")
            );

            // Configurar Embeddings (Fent servir OpenAI com a exemple)
            builder.AddOpenAITextEmbeddingGeneration("text-embedding-3-small", "LA_TEVA_OPENAI_KEY");

            var kernel = builder.Build();
            var embeddingService = kernel.GetRequiredService<ITextEmbeddingGenerationService>();
            var chatService = kernel.GetRequiredService<IChatCompletionService>();

            string connectionString = "Server=EL_TEU_SERVER;Database=RAG_DB;Integrated Security=True;TrustServerCertificate=True;";
            string folderPath = @"C:\ElsMeusDocuments";

            // --- 2. AGENT D'INGESTIÓ ---
            Console.WriteLine("Iniciant sincronització...");

            if (!Directory.Exists(folderPath))
            {
                Console.WriteLine("El directori no existeix.");
                return;
            }

            var files = Directory.GetFiles(folderPath, "*.*")
                .Where(f => f.EndsWith(".txt") || f.EndsWith(".pdf") || f.EndsWith(".docx"));

            foreach (var filePath in files)
            {
                var fileInfo = new FileInfo(filePath);

                if (await NeedsUpdateAsync(fileInfo, connectionString))
                {
                    Console.WriteLine($"Processant: {fileInfo.Name}...");
                    string text = await ExtractTextAsync(filePath);

                    // CORRECCIÓ TEXTCHUNKER:
                    // En versions recents, cal definir com es compten els tokens. 
                    // Si no vols complicacions, una aproximació és (caràcters / 4).
#pragma warning disable SKEXP0050 // Este tipo se incluye solo con fines de evaluación y está sujeto a cambios o a que se elimine en próximas actualizaciones. Suprima este diagnóstico para continuar.
                    var lines = TextChunker.SplitPlainTextLines(text, 500);
#pragma warning restore SKEXP0050 // Este tipo se incluye solo con fines de evaluación y está sujeto a cambios o a que se elimine en próximas actualizaciones. Suprima este diagnóstico para continuar.
#pragma warning disable SKEXP0050 // Este tipo se incluye solo con fines de evaluación y está sujeto a cambios o a que se elimine en próximas actualizaciones. Suprima este diagnóstico para continuar.
                    var chunks = TextChunker.SplitPlainTextParagraphs(lines, 500, 50);
#pragma warning restore SKEXP0050 // Este tipo se incluye solo con fines de evaluación y está sujeto a cambios o a que se elimine en próximas actualizaciones. Suprima este diagnóstico para continuar.

                    await CleanOldChunksAsync(fileInfo.Name, connectionString);

                    for (int i = 0; i < chunks.Count; i++)
                    {
                        var embedding = await embeddingService.GenerateEmbeddingAsync(chunks[i]);
                        await SaveChunkAsync(fileInfo, i, chunks[i], embedding, connectionString);
                    }
                    Console.WriteLine($"Fitxer {fileInfo.Name} guardat correctament.");
                }
            }

            // --- 3. AGENT DE CONSULTA (RAG) ---
            Console.WriteLine("\nLlest! Pregunta el que vulguis (o escriu 'sortir'):");
            while (true)
            {
                Console.Write("\nTu: ");
                var query = Console.ReadLine();
                if (query?.ToLower() == "sortir") break;

                var queryVector = await embeddingService.GenerateEmbeddingAsync(query);
                var context = await SearchSimilarContentAsync(queryVector, connectionString);

                var prompt = $@"Ets un assistent útil. Respon la pregunta basant-te NOMÉS en el context següent:
                ----------------
                {context}
                ----------------
                Pregunta: {query}";

                var response = await chatService.GetChatMessageContentAsync(prompt);
                Console.WriteLine($"\nIA (Groq): {response}");
            }
        }

        // --- MÈTODES AUXILIARS ---

        async static Task<bool> NeedsUpdateAsync(FileInfo file, string connStr)
        {
            try
            {
                using var conn = new SqlConnection(connStr);
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT TOP 1 LastModified FROM DocumentChunks WHERE FileName = @name", conn);
                cmd.Parameters.AddWithValue("@name", file.Name);
                var result = await cmd.ExecuteScalarAsync();
                if (result == null) return true;
                return (DateTime)result < file.LastWriteTimeUtc;
            }
            catch { return true; } // Si la taula no existeix encara, tornem true
        }

        async static Task<string> ExtractTextAsync(string path)
        {
            var ext = Path.GetExtension(path).ToLower();
            if (ext == ".pdf")
            {
                using var pdf = PdfDocument.Open(path);
                return string.Join("\n", pdf.GetPages().Select(p => p.Text));
            }
            if (ext == ".docx")
            {
                using var doc = WordprocessingDocument.Open(path, false);
                return doc.MainDocumentPart.Document.Body.InnerText;
            }
            return await File.ReadAllTextAsync(path);
        }

        async static Task SaveChunkAsync(FileInfo file, int index, string content, ReadOnlyMemory<float> vector, string connStr)
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            var cmd = new SqlCommand(@"INSERT INTO DocumentChunks (FileName, ChunkIndex, Content, Embedding, LastModified) 
                               VALUES (@name, @idx, @content, @vec, @last)", conn);
            cmd.Parameters.AddWithValue("@name", file.Name);
            cmd.Parameters.AddWithValue("@idx", index);
            cmd.Parameters.AddWithValue("@content", content);
            cmd.Parameters.AddWithValue("@vec", vector.ToArray());
            cmd.Parameters.AddWithValue("@last", file.LastWriteTimeUtc);
            await cmd.ExecuteNonQueryAsync();
        }

        async static Task<string> SearchSimilarContentAsync(ReadOnlyMemory<float> vector, string connStr)
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            var cmd = new SqlCommand(@"SELECT TOP 3 Content FROM DocumentChunks 
                               ORDER BY VECTOR_DISTANCE('cosine', Embedding, @vec) ASC", conn);
            cmd.Parameters.AddWithValue("@vec", vector.ToArray());

            using var reader = await cmd.ExecuteReaderAsync();
            var sb = new StringBuilder();
            while (await reader.ReadAsync()) sb.AppendLine(reader.GetString(0));
            return sb.ToString();
        }

        async static Task CleanOldChunksAsync(string fileName, string connStr)
        {
            using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();
            var cmd = new SqlCommand("DELETE FROM DocumentChunks WHERE FileName = @name", conn);
            cmd.Parameters.AddWithValue("@name", fileName);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}