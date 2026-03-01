// 1. Setup
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.SemanticKernel.Text;
using Microsoft.SemanticKernel.Connectors.Google;
using RAG_FITXERS;
using RAG_FITXERS.Services;
using RAG_FITXERS.Utils;

var builder = Kernel.CreateBuilder();

// Groq per a chat (compatible amb OpenAI)
builder.AddOpenAIChatCompletion(
    modelId: "llama3-8b-8192",
    apiKey: Environment.GetEnvironmentVariable("GROQ_KEY") ?? "LA_TEVA_CLAU",
    endpoint: new Uri("https://api.groq.com/openai/v1")
);

// Gemini per a embeddings
builder.AddGoogleAIEmbeddingGenerator(
    modelId: "text-embedding-004",
    apiKey: Environment.GetEnvironmentVariable("GEMINI_KEY") ?? "LA_TEVA_CLAU"
);

var kernel = builder.Build();

var db = new DatabaseService("Host=localhost;Username=postgres;Password=pass;Database=rag_db");
var orchestrator = new RagOrchestrator(kernel, db);

// 2. Ingestió
string folder = @"C:\Dades";

foreach (var path in Directory.GetFiles(folder, "*.*"))
{
    string hash = FileUtils.CalculateHash(path);
    if (await db.GetIdByHashAsync(hash) != null) continue;

    int docId = await db.RegisterDocumentAsync(Path.GetFileName(path), path, hash);
    string text = await FileUtils.ExtractAsync(path);

    // SplitPlainTextLines retorna IEnumerable<string>, cal .ToList()
    var lines = TextChunker.SplitPlainTextLines(text, 500).ToList();
    var chunks = TextChunker.SplitPlainTextParagraphs(lines, 500, 50);

    var embeddingService = kernel.GetRequiredService<ITextEmbeddingGenerationService>();

    for (int i = 0; i < chunks.Count; i++)
    {
        string searchable = $"[Doc: {Path.GetFileName(path)}] {chunks[i]}";

        // GenerateEmbeddingAsync retorna ReadOnlyMemory<float>, no cal .ToArray() directament
        ReadOnlyMemory<float> vec = await embeddingService.GenerateEmbeddingAsync(searchable);
        await db.SaveChunkAsync(docId, i, chunks[i], vec.ToArray());
    }
}

// 3. Xat
Console.WriteLine("Digues:");
string? q = Console.ReadLine();

if (!string.IsNullOrWhiteSpace(q))
{
    Console.WriteLine(await orchestrator.ProcessQueryAsync(q));
}