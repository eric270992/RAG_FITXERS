CREATE TABLE DocumentChunks (
    Id SERIAL PRIMARY KEY,
    DocumentId INTEGER NOT NULL,
    ChunkIndex INTEGER NOT NULL,      -- Ordre: 0, 1, 2... (Crucial pel Windowing)
    SectionTitle TEXT,                -- Títol de la secció (si es detecta)
    RawContent TEXT NOT NULL,         -- El text original del fragment
    Embedding vector(768),            -- El vector generat per Gemini (text-embedding-004)
    CreatedAt TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,

    -- Si esborrem un Document, s'esborren automàticament tots els seus chunks
    CONSTRAINT fk_document
      FOREIGN KEY(DocumentId) 
	  REFERENCES Documents(Id) 
	  ON DELETE CASCADE
);