-- Índex HNSW per a la cerca vectorial (Distància de Cosinus)
-- Aquest índex és molt més ràpid que el tradicional IVFFlat per a sistemes RAG
CREATE INDEX idx_chunks_embedding ON DocumentChunks 
USING hnsw (Embedding vector_cosine_ops);

-- Índex per al Windowing (per trobar ràpidament el fragment anterior i posterior)
CREATE INDEX idx_chunks_windowing ON DocumentChunks (DocumentId, ChunkIndex);