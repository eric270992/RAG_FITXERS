CREATE TABLE IF NOT EXISTS Documents (
    Id            SERIAL PRIMARY KEY,
    FileName      TEXT      NOT NULL,
    FilePath      TEXT      NOT NULL,    -- ← ruta al disc, suficient per al Nivell 4
    FileHash      TEXT      NOT NULL,
    Summary       TEXT,                  -- ← resum de 10 línies (Nivell 3)
    LastProcessed TIMESTAMP NOT NULL DEFAULT NOW(),

    CONSTRAINT unique_file_hash UNIQUE (FileHash)
);

-- Index per cerques ràpides per nom o hash
CREATE INDEX idx_documents_hash ON Documents (FileHash);
CREATE INDEX idx_documents_name ON Documents (FileName);