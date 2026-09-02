BEGIN;

ALTER TABLE public.leave_document_attachments
    ADD COLUMN IF NOT EXISTS file_content BYTEA NULL;

-- Keep legacy file-system records readable while new records use BYTEA.
ALTER TABLE public.leave_document_attachments
    ALTER COLUMN stored_file_name DROP NOT NULL,
    ALTER COLUMN storage_path DROP NOT NULL;

ALTER TABLE public.leave_document_attachments
    DROP CONSTRAINT IF EXISTS ck_leave_attachments_file_size;

ALTER TABLE public.leave_document_attachments
    ADD CONSTRAINT ck_leave_attachments_file_size
        CHECK (file_size_bytes > 0 AND file_size_bytes <= 10485760)
        NOT VALID;

ALTER TABLE public.leave_document_attachments
    DROP CONSTRAINT IF EXISTS ck_leave_attachments_content_size;

ALTER TABLE public.leave_document_attachments
    ADD CONSTRAINT ck_leave_attachments_content_size
        CHECK (file_content IS NULL OR OCTET_LENGTH(file_content) = file_size_bytes)
        NOT VALID;

COMMENT ON COLUMN public.leave_document_attachments.file_content IS
    'Binary attachment stored in PostgreSQL BYTEA; maximum size is 10 MB.';

CREATE INDEX IF NOT EXISTS ix_leave_document_attachments_document
    ON public.leave_document_attachments(leave_document_id);

COMMIT;
