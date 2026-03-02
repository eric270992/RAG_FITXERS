CREATE TABLE IF NOT EXISTS Documentchunks
(
    id SERIAL PRIMARY KEY, -- Esto crea la secuencia automáticamente
    documentid integer NOT NULL,
    chunkindex integer NOT NULL,
    sectiontitle text,
    rawcontent text NOT NULL,
    embedding vector(3072), -- Requiere extensión pgvector instalada
    createdat timestamp with time zone DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_document FOREIGN KEY (documentid)
        REFERENCES Documents (id) 
        ON UPDATE NO ACTION
        ON DELETE CASCADE
);

-- Es fundamental indexar la FK para que el DELETE CASCADE sea rápido
CREATE INDEX idx_documentchunks_documentid ON Documentchunks (documentid);