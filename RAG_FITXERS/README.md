# RAG_FITXERS — Sistema de Cerca Semàntica sobre Documents

> Projecte de **Retrieval-Augmented Generation (RAG)** en C# amb Semantic Kernel, Groq, Gemini i PostgreSQL + pgvector.

---

## Índex

- [Què és RAG?](#què-és-rag)
- [Què és un Vector o Embedding?](#què-és-un-vector-o-embedding)
- [Arquitectura del projecte](#arquitectura-del-projecte)
- [Classes principals](#classes-principals)
- [Com funciona PostgreSQL + pgvector](#com-funciona-postgresql--pgvector)
- [Sistema de cerca vectorial](#sistema-de-cerca-vectorial)
- [Flux complet](#flux-complet)
- [Configuració](#configuració)
- [Requisits](#requisits)

---

## Què és RAG?

**RAG (Retrieval-Augmented Generation)** és una tècnica que millora les respostes dels models de llenguatge (LLMs) combinant dos components:

1. **Recuperació (Retrieval):** Busca fragments de documents rellevants per a una pregunta concreta, usant cerca semàntica per vectors.
2. **Generació (Generation):** Un LLM genera la resposta final basant-se en el context recuperat, en lloc d'inventar-se la informació.

```
Pregunta de l'usuari
        │
        ▼
 Convertir a embedding (Gemini)
        │
        ▼
 Cerca vectorial a PostgreSQL
        │
        ▼
 Recuperar chunks rellevants
        │
        ▼
 Enviar context + pregunta al LLM (Groq / Llama)
        │
        ▼
 Resposta basada en els documents reals
```

**Per què usar RAG?**
- Els LLMs tenen un tall de coneixement (knowledge cutoff) i no coneixen els teus documents privats.
- RAG permet respondre preguntes sobre qualsevol document sense necessitat de re-entrenar el model.
- Les respostes estan fonamentades en fonts reals, reduint les al·lucinacions.

---

## Què és un Vector o Embedding?

Un **embedding** és una representació numèrica del significat semàntic d'un text, expressada com un vector de N dimensions (números decimals).

El model `gemini-embedding-001` de Google converteix qualsevol text en un vector de **3072 dimensions**.

### Exemple simplificat (en 2 dimensions):

```
"El gat menja peix"         → [0.91, 0.08]
"El felí s'alimenta de peix" → [0.88, 0.10]  ← MOLT PROPER (mateix significat)
"La borsa puja avui"         → [0.05, 0.97]  ← LLUNYÀ (diferent significat)
```

Texts amb **significat similar** produeixen vectors **propers en l'espai matemàtic**, independentment de les paraules exactes usades. Això permet fer cerques per significat, no per paraules clau.

### Distància cosinus (`<=>`)

La similitud entre dos vectors es mesura amb la **distància cosinus**, que calcula l'angle entre ells:

| Distància | Significat |
|-----------|------------|
| `0.0` | Vectors idèntics (màxima similitud) |
| `0.5` | Similitud moderada |
| `1.0` | Cap relació |
| `2.0` | Significats oposats |

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
```
