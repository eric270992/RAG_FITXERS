CREATE TABLE IF NOT EXISTS documentchunks
(
    id integer NOT NULL DEFAULT nextval('documentchunks_id_seq'::regclass),
    documentid integer NOT NULL,
    chunkindex integer NOT NULL,
    sectiontitle text COLLATE pg_catalog."default",
    rawcontent text COLLATE pg_catalog."default" NOT NULL,
    embedding vector(3072),
    createdat timestamp with time zone DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT documentchunks_pkey PRIMARY KEY (id),
    CONSTRAINT fk_document FOREIGN KEY (documentid)
        REFERENCES public.documents (id) MATCH SIMPLE
        ON UPDATE NO ACTION
        ON DELETE CASCADE
)