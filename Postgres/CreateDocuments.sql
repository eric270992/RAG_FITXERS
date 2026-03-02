CREATE TABLE Documents (
    Id SERIAL PRIMARY KEY,
    FileName TEXT NOT NULL,           -- Nom del fitxer (ej: manual.pdf)
    FilePath TEXT NOT NULL,           -- Ruta completa al disc
    FileHash TEXT NOT NULL,           -- Hash SHA256 del contingut per detectar canvis
    LastProcessed TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    
    -- Un index pel Hash ens permet saber instantàniament si ja tenim el fitxer
    CONSTRAINT unique_file_hash UNIQUE (FileHash)
);

-- Index per cerques ràpides per nom o hash
CREATE INDEX idx_documents_hash ON Documents (FileHash);
CREATE INDEX idx_documents_name ON Documents (FileName);