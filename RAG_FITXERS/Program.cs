// 1. Setup
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Text;
using RAG_FITXERS;
using RAG_FITXERS.Plugins;
using RAG_FITXERS.Services;
using RAG_FITXERS.Utils;

var builder = Kernel.CreateBuilder();

// Carregar la configuració des del fitxer
IConfiguration config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("configs.json", optional: false, reloadOnChange: true)
    .Build();

// Groq per a chat (compatible amb OpenAI)
builder.AddOpenAIChatCompletion(
    modelId: "llama-3.3-70b-versatile",
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
var embeddingService = kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var graphService = new GraphService(kernel, db.DataSource);

// Creem la classe amb tools
var ragPlugin = new RagPlugin(orchestrator);

// Les expose al kernel perquè siguin accessibles des dels prompts
kernel.Plugins.AddFromObject(ragPlugin, "DocumentsEmpresa");


// 2. Ingestió
string folder = config["Folders:PathToFiles"];

foreach (var path in Directory.GetFiles(folder, "*.*"))
{
    string fileName = Path.GetFileName(path);
    string hash = FileUtils.CalculateHash(path);

    if (await db.GetIdByHashAsync(hash) != null)
    {
        Console.WriteLine($"[SKIP] '{fileName}' ja processat.");
        continue;
    }

    Console.WriteLine($"[INFO] Processant '{fileName}'...");

    try
    {
        string text = await FileUtils.ExtractAsync(path);
        var lines = TextChunker.SplitPlainTextLines(text, 500).ToList();
        var rawChunks = TextChunker.SplitPlainTextParagraphs(lines, 500, 50);

        // Generem tots els embeddings ABANS de tocar la BD.
        // Així si falla Gemini a meitat, no hem escrit res a la BD
        // i el document es podrà tornar a processar íntegrament.
        Console.WriteLine($"[INFO] Generant embeddings per {rawChunks.Count} chunks...");
        var chunksAmbEmbeddings = new List<(string Content, float[] Embedding)>();

        foreach (var chunk in rawChunks)
        {
            string searchable = $"[Doc: {fileName}] {chunk}";
            var result = await embeddingService.GenerateAsync(new[] { searchable });
            chunksAmbEmbeddings.Add((chunk, result[0].Vector.ToArray()));
        }

        // Guardem document + chunks en una sola transacció atòmica.
        // Si falla qualsevol INSERT → ROLLBACK complet.
        await db.RegisterDocumentWithChunksAsync(fileName, path, hash, chunksAmbEmbeddings);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] Error processant '{fileName}': {ex.Message}");
        Console.WriteLine($"        El document es tornarà a processar en la propera execució.");
    }
}

// 3. Xat
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("\n╔════════════════════════════════════════════════════════╗");
Console.WriteLine("║        AGENT DOCUMENTAL — Benvingut/da!               ║");
Console.WriteLine("╠════════════════════════════════════════════════════════╣");
Console.WriteLine("║  Soc el teu assistent sobre els documents assignats.  ║");
Console.WriteLine("║  Fes-me qualsevol pregunta i intentaré ajudar-te.     ║");
Console.WriteLine("║  Escriu 'sortir' per acabar la sessió.                ║");
Console.WriteLine("╚════════════════════════════════════════════════════════╝\n");
Console.ResetColor();

while (true)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("Tu -> ");
    Console.ResetColor();

    string? q = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(q)) continue;
    if (q.Trim().ToLower() == "sortir") break;

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\nAgent -> Processant la teva consulta...\n");
    Console.ResetColor();

    string answer = await orchestrator.ProcessQueryAsync(q);

    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine($"Agent -> {answer}");
    Console.ResetColor();
    Console.WriteLine();
}

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("\nFins aviat! Sessió finalitzada.");
Console.ResetColor();