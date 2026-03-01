// 1. Setup
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.SemanticKernel.Text;
using RAG_FITXERS;
using RAG_FITXERS.Services;
using RAG_FITXERS.Utils;

var builder = Kernel.CreateBuilder();

// 1. Carregar la configuració des del fitxer
IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory()) // On es troba l'executable
    .AddJsonFile("configs.json", optional: false, reloadOnChange: true)
    .Build();

// Groq per a chat (compatible amb OpenAI)
builder.AddOpenAIChatCompletion(
    modelId: "llama3-8b-8192",
    apiKey: config["Keys:Groq"],
    endpoint: new Uri("https://api.groq.com/openai/v1"),
    serviceId: "groq"
);

// Gemini per a embeddings
builder.AddGoogleAIEmbeddingGenerator(
    modelId: "gemini-embedding-001",
    apiKey: config["Keys:Gemini"],
    serviceId: "gemini"
);

var kernel = builder.Build();

var db = new DatabaseService(config["ConnectionStrings:DefaultConnection"]);
var orchestrator = new RagOrchestrator(kernel, db);

// 2. Ingestió
string folder = config["Folders:PathToFiles"];

foreach (var path in Directory.GetFiles(folder, "*.*"))
{
    string hash = FileUtils.CalculateHash(path);
    if (await db.GetIdByHashAsync(hash) != null) continue;

    int docId = await db.RegisterDocumentAsync(Path.GetFileName(path), path, hash);
    string text = await FileUtils.ExtractAsync(path);

    var lines = TextChunker.SplitPlainTextLines(text, 500).ToList();
    var chunks = TextChunker.SplitPlainTextParagraphs(lines, 500, 50);

    var embeddingService = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

    for (int i = 0; i < chunks.Count; i++)
    {
        string searchable = $"[Doc: {Path.GetFileName(path)}] {chunks[i]}";

        var result = await embeddingService.GenerateAsync(new[] { searchable });
        float[] vec = result[0].Vector.ToArray();

        await db.SaveChunkAsync(docId, i, chunks[i], vec);
    }
}

// 3. Xat
Console.WriteLine("Digues:");
string? q = Console.ReadLine();

if (!string.IsNullOrWhiteSpace(q))
{
    Console.WriteLine(await orchestrator.ProcessQueryAsync(q));
}