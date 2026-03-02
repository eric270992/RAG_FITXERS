CREATE TABLE IF NOT EXISTS public.documents
(
    id integer NOT NULL DEFAULT nextval('documents_id_seq'::regclass),
    filename text COLLATE pg_catalog."default" NOT NULL,
    filepath text COLLATE pg_catalog."default" NOT NULL,
    filehash text COLLATE pg_catalog."default" NOT NULL,
    lastprocessed timestamp with time zone DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT documents_pkey PRIMARY KEY (id),
    CONSTRAINT unique_file_hash UNIQUE (filehash)
)

-- Index per cerques ràpides per nom o hash
CREATE INDEX idx_documents_hash ON Documents (FileHash);
CREATE INDEX idx_documents_name ON Documents (FileName);