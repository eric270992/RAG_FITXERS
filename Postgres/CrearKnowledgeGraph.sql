-- ============================================================
-- KnowledgeGraph — Graf de coneixement extret dels documents
--
-- Cada fila representa un triplet semàntic:
--   (Subject) → (Predicate) → (Object)
--
-- Exemple:
--   Joan García (Director) → director de → Departament de Logística
--   Departament Logística  → ha comprat  → Furgonetes elèctriques
--
-- Els triplets es generen durant la ingestió per l'agent LLM (Groq)
-- i permeten connectar informació entre documents que la cerca
-- vectorial no podria connectar per si sola.
-- ============================================================
CREATE TABLE IF NOT EXISTS KnowledgeGraph (
    Id          SERIAL PRIMARY KEY,

    -- Referència al document origen del triplet
    -- ON DELETE CASCADE: si s'elimina el document, s'eliminen els seus triplets
    DocumentId  INTEGER     NOT NULL,

    -- Referència al chunk concret d'on s'ha extret el triplet
    -- Permet localitzar l'origen exacte i fer windowing si cal
    -- ON DELETE CASCADE: si s'elimina el chunk, s'eliminen els seus triplets
    ChunkId     INTEGER     NOT NULL,

    -- Entitat principal de la relació
    -- Ha de ser sempre un nom complet per evitar ambigüitats
    -- Exemple correcte:   "Joan García (Director)"
    -- Exemple incorrecte: "Joan"
    Subject     TEXT        NOT NULL,

    -- Tipus de relació entre Subject i Object
    -- Ha de ser concís i descriptiu
    -- Exemple correcte:   "director de", "ha comprat", "pertany a"
    -- Exemple incorrecte: "té", "és"  ← massa genèrics
    Predicate   TEXT        NOT NULL,

    -- Entitat secundària de la relació
    -- Igual que Subject, ha de ser un nom complet
    -- Exemple correcte:   "Departament de Logística"
    -- Exemple incorrecte: "el departament"
    Object      TEXT        NOT NULL,

    CreatedAt   TIMESTAMP   NOT NULL DEFAULT NOW(),

    CONSTRAINT fk_knowledgegraph_document
        FOREIGN KEY (DocumentId)
        REFERENCES Documents(Id)
        ON DELETE CASCADE,

    CONSTRAINT fk_knowledgegraph_chunk
        FOREIGN KEY (ChunkId)
        REFERENCES DocumentChunks(Id)
        ON DELETE CASCADE
);

-- Cerca de relacions per Subject (cas més comú)
-- Exemple: "Dona'm totes les relacions on Joan García és el subjecte"
CREATE INDEX IF NOT EXISTS idx_graph_subject
    ON KnowledgeGraph (lower(Subject));

-- Cerca de relacions per Object
-- Exemple: "Qui té relació amb el Departament de Logística?"
CREATE INDEX IF NOT EXISTS idx_graph_object
    ON KnowledgeGraph (lower(Object));

-- Cerca de tots els triplets d'un document concret
CREATE INDEX IF NOT EXISTS idx_graph_document
    ON KnowledgeGraph (DocumentId);

-- Cerca de tots els triplets d'un chunk concret
CREATE INDEX IF NOT EXISTS idx_graph_chunk
    ON KnowledgeGraph (ChunkId);