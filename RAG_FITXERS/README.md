# RAG_FITXERS — Sistema de Cerca Semàntica sobre Documents

> Projecte de **Retrieval‑Augmented Generation (RAG)** en C# amb Semantic Kernel, Groq, Gemini i PostgreSQL + pgvector.

---

## Índex

- [Què és RAG?](#què-és-rag)  
- [Què és un Vector o Embedding?](#què-és-un-vector-o-embedding)  
- [Arquitectura del projecte](#arquitectura-del-projecte)  
- [Classes principals](#classes-principals)  
- [Com funciona PostgreSQL + pgvector](#com-funciona-postgresql--pgvector)  
- [Sistema de cerca vectorial](#sistema-de-cerca-vectorial)  
- [Sistema de triplets i fallback al context global](#sistema-de-triplets-i-fallback-al-context-global)  
- [Flux complet](#flux-complet)  
- [Configuració](#configuració)  
- [Requisits](#requisits)

---

## Què és RAG?

**RAG (Retrieval‑Augmented Generation)** combina recuperació semàntica de documents amb la generació d'un LLM per produir respostes fonamentades en contingut real. El pipeline recupera fragments rellevants i els usa com a context per al model de xat.

---

## Què és un Vector o Embedding?

Un **embedding** és una representació numèrica (vector) que codifica el significat d'un text. Vectors pròxims representen contingut semàntic similar. Al projecte s'utilitza `gemini-embedding-001` (3072 dimensions), i la similitud es mesura amb distància cosinus (`<=>` en `pgvector`).

---

## Arquitectura del projecte

```
RAG_FITXERS/
├── Program.cs                  → Punt d'entrada, ingestió de documents i xat
├── RagOrchestrator.cs          → Orquestrador del pipeline RAG (cerca + generació + judici)
├── Services/
│   └── DatabaseService.cs      → Accés a PostgreSQL (documents, chunks, cerca vectorial)
└── Utils/
    ├── FileUtils.cs            → Extracció de text de fitxers (PDF, DOCX, TXT...)
    └── FileHash.cs             → Càlcul de hash per deduplicació de documents
```

---

## Classes principals

### `Program.cs`
Punt d'entrada de l'aplicació. S'encarrega de:
- Configurar els serveis (Groq, Gemini, PostgreSQL) mitjançant `configs.json`.
- **Ingestió:** Iterar sobre els fitxers d'una carpeta, calcular el hash, chunketjar el text, generar embeddings amb Gemini i desar-ho tot a PostgreSQL en una transacció atòmica.
- **Xat:** Llegir la pregunta de l'usuari i delegar a `RagOrchestrator`.

### `RagOrchestrator`
Orquestrador del pipeline RAG. S'encarrega de:
- Convertir la pregunta de l'usuari en un embedding (Gemini).
- Recuperar el context rellevant de la BD (`GetContextWindowAsync`).
- Generar la resposta amb el LLM (Groq / Llama).
- Avaluar la qualitat de la resposta mitjançant un **Judge** (LLM que puntua de 0 a 100).
- Reintentar la generació si la puntuació és inferior a 80.

### `DatabaseService`
Gestiona tota la interacció amb PostgreSQL. Mètodes principals:

| Mètode | Descripció |
|--------|------------|
| `GetIdByHashAsync(hash)` | Comprova si un document ja ha estat processat (deduplicació) |
| `RegisterDocumentWithChunksAsync(...)` | Desa document + tots els chunks en una **transacció atòmica** |
| `GetContextWindowAsync(vector)` | Cerca vectorial + windowing per recuperar context rellevant |

### `FileUtils`
Extreu el text llegible de diferents formats de fitxer (`.txt`, `.pdf`, `.docx`, etc.) per poder-lo chunketjar i indexar.

---

## Com funciona PostgreSQL + pgvector

[pgvector](https://github.com/pgvector/pgvector) és una extensió de PostgreSQL que afegeix suport natiu per a vectors de N dimensions, permetent emmagatzemar embeddings i fer cerques per similitud directament a la base de dades.

### Esquema de la base de dades

```sql
-- Habilitar l'extensió
CREATE EXTENSION IF NOT EXISTS vector;

-- Documents originals
CREATE TABLE Documents (
    Id       SERIAL PRIMARY KEY,
    FileName TEXT NOT NULL,
    FilePath TEXT NOT NULL,
    FileHash TEXT UNIQUE NOT NULL,   -- Per detectar duplicats
    CreatedAt TIMESTAMP DEFAULT NOW()
);

-- Fragments del document amb el seu embedding
CREATE TABLE DocumentChunks (
    Id         SERIAL PRIMARY KEY,
    DocumentId INTEGER REFERENCES Documents(Id) ON DELETE CASCADE,
    ChunkIndex INTEGER NOT NULL,
    RawContent TEXT NOT NULL,          -- Text original llegible
    Embedding  VECTOR(3072),           -- Vector de 3072 dimensions (gemini-embedding-001)
    CreatedAt  TIMESTAMP DEFAULT NOW()
);

-- Índex per accelerar les cerques per similitud
CREATE INDEX chunks_embedding_idx
ON DocumentChunks USING ivfflat (Embedding vector_cosine_ops);
```

### Per què 3072 dimensions?
El model `gemini-embedding-001` de Google genera vectors de 3072 dimensions per defecte, proporcionant una representació semàntica molt rica i precisa.

---

## Sistema de cerca vectorial

La cerca semàntica es fa en dos passos:

### Pas 1: Cerca vectorial
S'usa l'operador `<=>` de pgvector (distància cosinus) per trobar el chunk més similar a la pregunta:

```sql
SELECT DocumentId, ChunkIndex
FROM DocumentChunks
ORDER BY Embedding <=> @queryVector
LIMIT 1;
```

### Pas 2: Windowing (finestra de context)
No es retorna només el chunk trobat, sinó també els seus **veïns immediats** (anterior i posterior). Això és fonamental perquè la informació rellevant sovint queda repartida entre chunks consecutius.

```sql
SELECT RawContent
FROM DocumentChunks
WHERE DocumentId = @docId
  AND ChunkIndex BETWEEN @centerIdx - 1 AND @centerIdx + 1
ORDER BY ChunkIndex;
```

```
Chunk N-1: "...el tractament consisteix en..."   ← veí anterior
Chunk N:   "...dosi recomanada és 500mg..."       ← chunk trobat ✓
Chunk N+1: "...efectes secundaris inclouen..."   ← veí posterior
           ─────────────────────────────────
           Context complet enviat al LLM
```

---

## Sistema de triplets i fallback al context global

Per millorar la precisió en dominis específics, s'implementa un sistema de [triplets de retroalimentació (feedback triplets)](#), on el model pot recuperar informació addicional sobre:

1. **El document més rellevant**
2. **El fragment més rellevant**
3. **Una resposta generada prèviament**

En cas que la resposta generada no superi un llindar de confiança, es fa un **fallback** a un context global o a un fragment alternatiu del document.

### Exemple de triplet

```sql
SELECT RawContent
FROM DocumentChunks
WHERE DocumentId = @docId
  AND ChunkIndex IN (@relevantChunkIdx, @alternativeChunkIdx)
ORDER BY ChunkIndex;
```

---

## Flux complet

### Fase d'ingestió (una sola vegada per document)

```
Fitxer
  │
  ├─ Calcular hash → Ja existeix? → SKIP
  │
  ├─ Extreure text (FileUtils)
  │
  ├─ Chunketjar (TextChunker)
  │     SplitPlainTextLines(text, 500)       → Línies de màx. 500 tokens
  │     SplitPlainTextParagraphs(lines, 500, 50) → Chunks amb 50 tokens de solapament
  │
  ├─ Generar embeddings (Gemini gemini-embedding-001)
  │     "[Doc: nom.pdf] contingut del chunk..."  → float[3072]
  │
  └─ Desar a PostgreSQL (transacció atòmica)
        INSERT Documents
        INSERT DocumentChunks × N
```

### Fase de consulta (cada pregunta)

```
Pregunta de l'usuari
  │
  ├─ Generar embedding de la pregunta (Gemini)
  │
  ├─ Cerca vectorial a PostgreSQL (<=>)
  │
  ├─ Windowing: recuperar chunk central + veïns
  │
  ├─ Generar resposta (Groq / Llama 3.3 70B)
  │
  ├─ Judge: puntuar resposta (0-100)
  │     < 80 → Reintentar (màx. 2 intents)
  │     ≥ 80 → Retornar resposta
  │
  └─ Mostrar resposta a l'usuari
```

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
