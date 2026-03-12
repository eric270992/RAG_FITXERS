# RAG_FITXERS — Sistema de Cerca Semàntica sobre Documents

> Projecte de **Retrieval‑Augmented Generation (RAG)** en C# amb Semantic Kernel, Groq, Gemini i PostgreSQL + pgvector.

---

## Índex

- [Què és RAG?](#què-és-rag)
- [Què és un Vector o Embedding?](#què-és-un-vector-o-embedding)
- [Arquitectura del projecte](#arquitectura-del-projecte)
- [Classes principals](#classes-principals)
- [Esquema de la base de dades](#esquema-de-la-base-de-dades)
- [Fase dingestió](#fase-dingestió)
- [Fase de consulta](#fase-de-consulta)
- [Sistema de Windowing](#sistema-de-windowing)
- [GraphRAG — Triplets i connexions creuades](#graphrag--triplets-i-connexions-creuades)
- [Hierarchical Retrieval — 4 nivells](#hierarchical-retrieval--4-nivells)
- [Sistema de Judge](#sistema-de-judge)
- [Consum de tokens i optimització](#consum-de-tokens-i-optimització)
- [Configuració](#configuració)
- [Requisits](#requisits)

---

## Què és RAG?

**RAG (Retrieval‑Augmented Generation)** és una tècnica que combina dos components:

1. **Recuperació semàntica** — donat un document o conjunt de documents, es converteix el seu contingut en vectors numèrics i es desen a una base de dades vectorial. Quan l'usuari fa una pregunta, aquesta es converteix al mateix espai vectorial i es recuperen els fragments més similars.

2. **Generació augmentada** — els fragments recuperats s'envien com a context a un LLM (Large Language Model), que genera una resposta fonamentada en el contingut real dels documents, no en el seu coneixement intern.

L'avantatge principal respecte a enviar tots els documents al LLM és el **cost**: en lloc d'enviar centenars de pàgines cada vegada, només s'envia el fragment rellevant. A més, la resposta és verificable i traçable fins al document original.

---

## Què és un Vector o Embedding?

Un **embedding** és una representació numèrica d'un text en forma de vector de N dimensions. La idea fonamental és que textos amb significat similar produeixen vectors pròxims en aquest espai.

```
"El director de logística és Joan"   → [0.12, -0.87, 0.34, ..., 0.91]  (3072 valors)
"Joan dirigeix el departament"       → [0.11, -0.85, 0.36, ..., 0.89]  (molt proper!)
"La temperatura avui és de 22 graus" → [-0.54, 0.23, -0.71, ..., 0.12] (molt llunyà)
```

La distància entre dos vectors mesura la seva similitud semàntica. El projecte usa **distància cosinus** (`<=>` en pgvector), que mesura l'angle entre vectors independentment de la seva magnitud.

El model `gemini-embedding-001` de Google genera vectors de **3072 dimensions**, proporcionant una representació semàntica molt rica que captura matisos de significat, sinònims i relacions conceptuals.

---

## Arquitectura del projecte

```
RAG_FITXERS/
├── Program.cs                   → Punt d'entrada: configuració, ingestió i xat
├── RagOrchestrator.cs           → Pipeline RAG complet (cerca + generació + judge)
├── Services/
│   ├── DatabaseService.cs       → Accés a PostgreSQL (documents, chunks, cerca vectorial)
│   └── GraphService.cs          → Extracció i cerca de triplets (GraphRAG)
├── Models/
│   ├── ExtractionResult.cs      → Model de resposta del LLM per a triplets
│   └── JudgeResult.cs           → Model de resposta del Judge (score + reason)
├── Plugins/
│   └── RagPlugin.cs             → Exposició de funcions RAG com a tools per a agents
└── Utils/
    ├── FileUtils.cs             → Extracció de text de fitxers (PDF, DOCX, TXT...)
    └── FileHash.cs              → Càlcul de hash SHA256 per a deduplicació
```

---

## Classes principals

### `Program.cs`

Punt d'entrada de l'aplicació. Té tres responsabilitats:

**1. Configuració dels serveis:**
```csharp
// LLM de xat: Groq amb Llama 3.3 70B (compatible amb API OpenAI)
builder.AddOpenAIChatCompletion(
    modelId: "llama-3.3-70b-versatile",
    apiKey: config["Keys:Groq"],
    endpoint: new Uri("https://api.groq.com/openai/v1"),
    serviceId: "groq"
);

// Generació d'embeddings: Gemini gemini-embedding-001 (3072 dimensions)
builder.AddGoogleAIEmbeddingGenerator(
    modelId: "gemini-embedding-001",
    apiKey: config["Keys:Gemini"],
    serviceId: "gemini"
);
```

**2. Ingestió de documents** (vegeu [Fase d'ingestió](#fase-dingestió))

**3. Bucle de xat:**
```
Llegir pregunta → RagOrchestrator.ProcessQueryAsync → Mostrar resposta
```

La distinció important és que els serveis d'IA (Groq, Gemini) es registren al **Kernel** i s'obtenen via `kernel.GetRequiredService<T>()`, mentre que les classes pròpies del projecte (`DatabaseService`, `GraphService`, `RagOrchestrator`) s'instancien directament amb `new` i es passen com a paràmetres als constructors.

---

### `RagOrchestrator`

Orquestra el pipeline RAG complet. És el cervell del sistema.

Depèn de:
- `IChatCompletionService` (Groq) — per generar respostes i jutjar-les
- `IEmbeddingGenerator` (Gemini) — per convertir preguntes a vectors
- `DatabaseService` — per cercar chunks rellevants
- `GraphService` — per cercar triplets i connexions creuades

Mètodes principals:

| Mètode | Descripció |
|--------|------------|
| `ProcessQueryAsync(question)` | Pipeline complet: embedding → cerca → generació → judge → reintents |
| `GenerateAsync(question, context)` | Genera resposta amb Groq donat un context |
| `JudgeAsync(question, context, answer)` | Avalua la qualitat de la resposta (0-100) |
| `GetFullDocumentAsync(question)` | Nivell 4: llegeix el document sencer des del disc |

---

### `DatabaseService`

Gestiona tota la interacció amb PostgreSQL + pgvector.

| Mètode | Descripció |
|--------|------------|
| `GetIdByHashAsync(hash)` | Comprova si un document ja ha estat processat (deduplicació per hash SHA256) |
| `RegisterDocumentWithChunksAsync(...)` | Desa document + N chunks en una **transacció atòmica**. Retorna `(docId, List<chunkIds>)` |
| `GetContextWindowAsync(vector, windowSize)` | Cerca vectorial + windowing. Retorna el text dels chunks |
| `GetContextWindowWithIdsAsync(vector, windowSize)` | Igual però retorna també els ChunkIds per poder buscar triplets |
| `GetChunksByIdsAsync(chunkIds)` | Recupera el text de chunks concrets per ID (usat per als chunks de connexions creuades) |
| `GetDocumentSummaryAsync(vector)` | Retorna el resum del document més rellevant (Nivell 3) |
| `GetMostRelevantFilePathAsync(vector)` | Retorna la ruta del fitxer del document més rellevant (Nivell 4) |

---

### `GraphService`

Gestiona el sistema de **GraphRAG** basat en triplets (Subject → Predicate → Object).

| Mètode | Descripció |
|--------|------------|
| `ProcessChunkAsync(docId, chunkId, text)` | Demana al LLM que extregui triplets del chunk i els desa a `KnowledgeGraph` |
| `GetGraphContextAsync(question)` | Extreu entitats de la pregunta i busca relacions directament a `KnowledgeGraph` |
| `GetTripletsByChunkIdsAsync(chunkIds)` | Busca triplets dels chunks trobats per cerca vectorial + connexions creuades. Retorna `(triplets, crossChunkIds)` |

---

## Esquema de la base de dades

```sql
CREATE EXTENSION IF NOT EXISTS vector;

-- Documents originals
CREATE TABLE Documents (
    Id            SERIAL PRIMARY KEY,
    FileName      TEXT      NOT NULL,
    FilePath      TEXT      NOT NULL,    -- Ruta al disc per al Nivell 4
    FileHash      TEXT      NOT NULL,    -- SHA256 per a deduplicació
    Summary       TEXT,                  -- Resum del document (Nivell 3)
    LastProcessed TIMESTAMP NOT NULL DEFAULT NOW(),
    CONSTRAINT unique_file_hash UNIQUE (FileHash)
);

-- Fragments del document amb el seu embedding
CREATE TABLE DocumentChunks (
    Id           SERIAL PRIMARY KEY,
    DocumentId   INTEGER      NOT NULL,
    ChunkIndex   INTEGER      NOT NULL,  -- Posició del chunk dins el document
    SectionTitle TEXT,                   -- Títol de secció si es detecta
    RawContent   TEXT         NOT NULL,  -- Text original llegible
    Embedding    VECTOR(3072) NOT NULL,  -- Vector de 3072 dimensions (Gemini)
    CreatedAt    TIMESTAMP    NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_document FOREIGN KEY (DocumentId) REFERENCES Documents(Id) ON DELETE CASCADE,
    CONSTRAINT uq_chunk_index UNIQUE (DocumentId, ChunkIndex)
);

-- Índex HNSW per a cerques vectorials ràpides
CREATE INDEX idx_chunks_embedding ON DocumentChunks USING hnsw (Embedding vector_cosine_ops);

-- Graf de coneixement: triplets (Subject → Predicate → Object)
CREATE TABLE KnowledgeGraph (
    Id          SERIAL PRIMARY KEY,
    DocumentId  INTEGER NOT NULL,
    ChunkId     INTEGER NOT NULL,       -- Origen exacte del triplet
    Subject     TEXT    NOT NULL,       -- Entitat principal: "Joan García (Director)"
    Predicate   TEXT    NOT NULL,       -- Relació: "director de", "ha comprat"
    Object      TEXT    NOT NULL,       -- Entitat o valor: "Departament de Logística"
    CreatedAt   TIMESTAMP NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_kg_document FOREIGN KEY (DocumentId) REFERENCES Documents(Id) ON DELETE CASCADE,
    CONSTRAINT fk_kg_chunk    FOREIGN KEY (ChunkId)    REFERENCES DocumentChunks(Id) ON DELETE CASCADE
);

-- Índexos per a cerques per entitat (connexions creuades)
CREATE INDEX idx_graph_subject  ON KnowledgeGraph (lower(Subject));
CREATE INDEX idx_graph_object   ON KnowledgeGraph (lower(Object));
```

---

## Fase d'ingestió

La ingestió es fa **una sola vegada per document**. Si el document no ha canviat (mateix hash SHA256), es salta (`[SKIP]`).

```
Fitxer al disc
      │
      ├─ 1. Calcular hash SHA256
      │      └─ Ja existeix a la BD? → SKIP
      │
      ├─ 2. Extreure text (FileUtils.ExtractAsync)
      │      Suporta: .txt, .pdf, .docx, etc.
      │
      ├─ 3. Chunketjar el text (Semantic Kernel TextChunker)
      │      SplitPlainTextLines(text, 500)           → Línies de màx. 500 tokens
      │      SplitPlainTextParagraphs(lines, 500, 50) → Chunks amb 50 tokens de solapament
      │
      ├─ 4. Generar embeddings (Gemini) per a cada chunk
      │      "[Doc: nom.pdf] contingut del chunk..." → float[3072]
      │      ⚠️ Es fan TOTS els embeddings ABANS de tocar la BD.
      │         Si falla Gemini a meitat, no s'ha escrit res i es pot reintentar.
      │
      ├─ 5. Transacció atòmica a PostgreSQL
      │      DELETE document anterior (si existia amb el mateix nom)
      │      INSERT Documents → obté docId
      │      INSERT DocumentChunks × N → obté List<chunkId>
      │      Si falla qualsevol INSERT → ROLLBACK complet
      │
      └─ 6. Extracció de triplets (GraphRAG)
             Per cada chunk:
               ProcessChunkAsync(docId, chunkId, text)
               → LLM extreu triplets (Subject → Predicate → Object)
               → Filtre: Confidence >= 80
               → INSERT KnowledgeGraph
```

**Per què els embeddings es generen ABANS de la transacció?**

Si es generessin dins la transacció i Gemini fallés al chunk 47 de 50, la transacció hauria de fer rollback i es perdria tot el treball. Generant-los tots primer, si falla Gemini, cap dada s'ha escrit a la BD i el document es podrà reprocessar íntegrament en la propera execució.

---

## Fase de consulta

Cada pregunta de l'usuari segueix aquest flux:

```
Pregunta de l'usuari
      │
      ▼
1. Generar embedding de la pregunta (Gemini)
      │
      ▼
2. Bucle de reintents (mentre score < 80 i attempts <= 2):
      │
      ├─ GetContextWindowWithIdsAsync → chunks principals + ChunkIds
      │
      ├─ GetTripletsByChunkIdsAsync(chunkIds)
      │    → Triplets directes dels chunks principals
      │    → Connexions creuades (altres documents via entitats compartides)
      │    → CrossChunkIds (chunks dels documents connectats)
      │
      ├─ Si attempts > 0: GetChunksByIdsAsync(crossChunkIds)
      │    → Text complet dels chunks de connexions creuades
      │
      ├─ Construir fullContext (vectorContext + graphContext + crossContext)
      │
      ├─ GenerateAsync(question, fullContext) → resposta
      │
      └─ JudgeAsync(question, fullContext, answer) → score (0-100)
           score >= 80 → retornar resposta
           score <  80 → ampliar windowSize i reintentar
      │
      ▼
3. Si score < 80 després de 3 intents → Nivell 4
      GetFullDocumentAsync(question) → document sencer des del disc
      GenerateAsync(question, fullDocument) → resposta final (sense Judge)
```

---

## Sistema de Windowing

La cerca vectorial troba el chunk **més similar** a la pregunta. Però la informació rellevant sovint queda repartida entre chunks consecutius. El windowing soluciona això retornant el chunk central més els seus veïns:

```
Document "organigrama.txt"
  Chunk 3: "...context anterior..."
  Chunk 4: "...context anterior..."    ← windowSize=2: s'inclou
  Chunk 5: "Joan García, director..."  ← windowSize=1: s'inclou
  Chunk 6: "Salari: 45.000€..."        ← CHUNK TROBAT (centerIdx=6)
  Chunk 7: "Reporta a: Miquel Soler"   ← windowSize=1: s'inclou
  Chunk 8: "...context posterior..."   ← windowSize=2: s'inclou
  Chunk 9: "...context posterior..."
```

```sql
-- PAS 1: Troba el chunk més proper semànticament
SELECT DocumentId, ChunkIndex
FROM DocumentChunks
ORDER BY Embedding <=> @queryVector
LIMIT 1;
-- Resultat: DocumentId=1, ChunkIndex=6

-- PAS 2: Recupera el chunk central + veïns (windowSize=1 → 3 chunks)
SELECT Id, RawContent
FROM DocumentChunks
WHERE DocumentId = 1
  AND ChunkIndex BETWEEN 5 AND 7   -- centerIdx-windowSize a centerIdx+windowSize
ORDER BY ChunkIndex;
-- Retorna ChunkIds: [10, 11, 12] i el text de cadascun
```

El `windowSize` creix amb cada reintent del Judge:
- Intent 1 → windowSize=1 → 3 chunks
- Intent 2 → windowSize=2 → 5 chunks
- Intent 3 → windowSize=3 → 7 chunks

---

## GraphRAG — Triplets i connexions creuades

El GraphRAG permet connectar informació dispersa en múltiples documents mitjançant un **graf de coneixement** basat en triplets.

### Estructura d'un triplet

```
Subject                   Predicate           Object
──────────────────────    ──────────────      ──────────────────────
Joan García               director de         Departament Logística
RenderFarm Estudi         pressupost          60.000 €
COMP-2024-045             destinació          RenderFarm Estudi
Jordi Amat Bosc           ha autoritzat       COMP-2024-045
```

Cada triplet sap de quin `DocumentId` i `ChunkId` prové, cosa que permet traçar l'origen de cada relació.

### Extracció durant la ingestió

Per cada chunk, el LLM (Groq) analitza el text i extreu els triplets amb un prompt genèric:

```
Chunk: "Les targetes RTX 4090 van ser comprades per al projecte RenderFarm Estudi
        amb un import de 35.000 €, autoritzat per Jordi Amat Bosc."

Triplets extrets:
  COMP-2024-045    → destinació    → RenderFarm Estudi    (Confidence: 95) ✓
  COMP-2024-045    → import        → 35.000 €             (Confidence: 98) ✓
  Jordi Amat Bosc  → ha autoritzat → COMP-2024-045        (Confidence: 92) ✓
  RTX 4090         → és part de   → COMP-2024-045         (Confidence: 75) ✗ (< 80)
```

Només s'emmagatzemen triplets amb **Confidence >= 80**. El prompt és intencionadament **genèric** (no especifica categories com "busca pressupostos" o "busca persones") perquè el LLM infereixi quines relacions són importants en qualsevol tipus de document.

### Cerca de triplets pas a pas

Quan l'usuari fa una pregunta, `GetTripletsByChunkIdsAsync` fa tres passos:

**PAS 1 — Triplets directes** dels chunks trobats per cerca vectorial (ex: ChunkIds = [9, 10, 11]):
```sql
SELECT d.FileName, kg.Subject, kg.Predicate, kg.Object
FROM KnowledgeGraph kg
INNER JOIN Documents d ON d.Id = kg.DocumentId
WHERE kg.ChunkId IN (9, 10, 11)
```
Resultat:
```
[compres_2024.txt] COMP-2024-045   → destinació    → RenderFarm Estudi
[compres_2024.txt] COMP-2024-045   → import        → 35.000 €
[compres_2024.txt] Jordi Amat Bosc → ha autoritzat → COMP-2024-045
```

**PAS 2 — Recollida d'entitats** (Subject i Object, NO el Predicate):
```
entities = {"COMP-2024-045", "RenderFarm Estudi", "35.000 €", "Jordi Amat Bosc"}
```
El Predicate (ex: "destinació", "ha autoritzat") NO s'inclou perquè és una relació, no una entitat. Buscar "destinació" a tot el KnowledgeGraph retornaria triplets no relacionats, generant soroll.

**PAS 3 — Connexions creuades** per a cada entitat, excloent els chunks que ja tenim:
```sql
-- Per a l'entitat "RenderFarm Estudi":
SELECT kg.ChunkId, d.FileName, kg.Subject, kg.Predicate, kg.Object
FROM KnowledgeGraph kg
INNER JOIN Documents d ON d.Id = kg.DocumentId
WHERE (lower(kg.Subject) LIKE lower('%RenderFarm Estudi%')
   OR  lower(kg.Object)  LIKE lower('%RenderFarm Estudi%'))
AND kg.ChunkId NOT IN (9, 10, 11)   -- excloem els chunks que ja tenim
```
Resultat (d'un document diferent!):
```
[projectes_actius.txt] RenderFarm Estudi → pressupost   → 60.000 €   ← SALT!
[projectes_actius.txt] Elena Puig Gual   → responsable  → RenderFarm Estudi
```

El mètode també retorna els `CrossChunkIds` ([25, 26, ...]) per si cal recuperar el text complet d'aquests chunks al segon intent.

### El "salt" entre documents visualitzat

```
CERCA VECTORIAL                        CONNEXIÓ CREUADA
troba chunks 9, 10, 11                 troba chunk 25
(compres_2024.txt)                     (projectes_actius.txt)
──────────────────                     ─────────────────────────────
COMP-2024-045                          RenderFarm Estudi
  → import      → 35.000 €              → pressupost → 60.000 €  ✓
  → destinació  → RenderFarm Estudi     → responsable → Elena Puig
                       │
                       │ "RenderFarm Estudi" apareix com a Object
                       │ → Busquem a TOT KnowledgeGraph
                       │ → excloent ChunkIds 9, 10, 11
                       └──────────────────────────────────────────►
                                         Trobem ChunkId=25
                                         d'un document diferent!
```

Sense GraphRAG, la cerca vectorial mai hauria trobat el pressupost de 60.000 € perquè la pregunta sobre "RTX 4090" no és semànticament propera al text "pressupost assignat de 60.000 €". El pont és l'entitat compartida "RenderFarm Estudi".

---

## Hierarchical Retrieval — 4 nivells

El sistema implementa una estratègia de recuperació jerarquitzada. Cada nivell s'activa quan l'anterior no ha estat suficient (score del Judge < 80):

```
Nivell 1+2 — Cerca vectorial + GraphRAG
  Chunks del windowing + triplets directes + connexions creuades
  Cost baix (~400 tokens per intent)
  Reintents: windowSize creix de 1 a 3, afegint crossContext al 2n intent

Nivell 3 — Resum del document
  Camp Summary de la taula Documents
  Cost moderat
  S'activa si el Judge segueix donant score < 80

Nivell 4 — Document sencer (últim recurs)
  Llegeix el fitxer complet des del disc via FilePath
  Cost alt (~5000 tokens)
  NO passa pel Judge (és el màxim possible)
  No es guarda el text complet a la BD per estalviar espai
```

**Per què el Nivell 4 llegeix del disc i no de la BD?**

Guardar el text complet de cada document a la BD seria costós en espai i redundant, ja que els chunks ja contenen tot el text. El camp `FilePath` a la taula `Documents` permet llegir el fitxer original quan sigui necessari sense duplicar dades.

---

## Sistema de Judge

El Judge és un segon LLM (també Groq) que avalua si la resposta generada és suficientment bona per a la pregunta donada, retornant un JSON estructurat:

```csharp
var prompt = $@"Avalua la resposta (0-100) segons el context.
               Respon NOMÉS en JSON: {{""score"": 80, ""reason"": ""...""}}
               CONTEXT: {context}
               PREGUNTA: {question}
               RESPOSTA: {answer}";

// Exemples de resposta:
// { "score": 85, "reason": "La resposta menciona el pressupost correctament" }
// { "score": 42, "reason": "No s'ha trobat informació sobre el model dels sensors" }
```

Lògica de reintents:
```
score >= 80 → resposta acceptable → retornar
score <  80 → ampliar windowSize → reintentar (màx. 3 intents)
3 intents fallats → Nivell 4 (document sencer, sense Judge)
```

El Judge és especialment important per a les **preguntes trampa** (informació no present als documents). En lloc d'al·lucinar una resposta, el sistema hauria de reconèixer que no té prou informació i retornar-ho explícitament al Nivell 4.

---

## Consum de tokens i optimització

El sistema optimitza el consum de tokens segmentant la cerca en dos modes:

### Mode simple (Intent 1)
```
vectorContext  → ~300 tokens  (3 chunks del windowing)
graphContext   → ~100 tokens  (triplets en format compacte)
─────────────────────────────
Total          → ~400 tokens
```

### Mode avançat (Intents 2 i 3)
Actiu quan el Judge ha detectat que falta informació:
```
vectorContext  → ~500-700 tokens  (5-7 chunks, windowSize ampliat)
crossContext   → ~300 tokens      (text dels chunks de connexions creuades)
graphContext   → ~150-200 tokens  (més triplets)
─────────────────────────────────
Total          → ~950-1100 tokens
```

### Nivell 4 (últim recurs)
```
fullDocument   → ~5000 tokens    (document sencer)
```

**Per què els triplets ja són suficients al primer intent?**

Un triplet com `RenderFarm Estudi → pressupost → 60.000 €` conté la informació clau en ~10 tokens. El text complet del chunk d'on prové pot ocupar ~100 tokens però no sempre aporta informació addicional rellevant. Per això el text dels chunks creuats (`crossContext`) es reserva per al segon intent, quan el Judge confirma que falta informació.

---

## Configuració

Crea un fitxer `configs.json` a l'arrel del projecte:

```json
{
  "Keys": {
    "Groq": "LA_TEVA_CLAU_GROQ",
    "Gemini": "LA_TEVA_CLAU_GEMINI"
  },
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Username=postgres;Password=pass;Database=rag_db"
  },
  "Folders": {
    "PathToFiles": "C:\\Dades"
  }
}
```

---

## Requisits

| Component | Versió |
|-----------|--------|
| .NET | 8.0+ |
| PostgreSQL | 15+ |
| pgvector | 0.5+ |
| Microsoft.SemanticKernel | Darrera versió estable |
| Microsoft.SemanticKernel.Connectors.Google | Darrera versió estable |
| Npgsql | 7.0+ |
| Pgvector (NuGet) | Darrera versió estable |

### Docker (recomanat per a PostgreSQL + pgvector)

```yaml
services:
  postgres:
    image: pgvector/pgvector:pg16
    environment:
      POSTGRES_PASSWORD: pass
      POSTGRES_DB: rag_db
    ports:
      - "5432:5432"
```

### Models locals (alternativa a Groq + Gemini)

El sistema és compatible amb models locals via Ollama o LM Studio (mateixa API OpenAI):

```json
{
  "LocalModels": {
    "Endpoint": "http://localhost:11434/v1",
    "Chat": "qwen2.5:14b",
    "Embedding": "bge-m3"
  }
}
```

Amb models locals, cal canviar `VECTOR(3072)` a `VECTOR(1024)` a l'esquema de la BD (el model `bge-m3` genera vectors de 1024 dimensions).
