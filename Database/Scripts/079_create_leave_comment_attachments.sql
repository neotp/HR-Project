BEGIN;

CREATE TABLE IF NOT EXISTS public.leave_comment_attachments
(
    id                 BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_comment_id   BIGINT NOT NULL
        REFERENCES public.leave_document_comments(id) ON DELETE CASCADE,
    original_file_name VARCHAR(255) NOT NULL,
    content_type       VARCHAR(100) NOT NULL,
    file_size_bytes    BIGINT NOT NULL,
    file_content       BYTEA NOT NULL,
    uploaded_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_leave_comment_attachment_type
        CHECK (content_type IN ('image/png', 'image/jpeg', 'image/webp')),
    CONSTRAINT ck_leave_comment_attachment_size
        CHECK (file_size_bytes BETWEEN 1 AND 3145728),
    CONSTRAINT ck_leave_comment_attachment_content
        CHECK (OCTET_LENGTH(file_content) = file_size_bytes)
);

CREATE INDEX IF NOT EXISTS ix_leave_comment_attachments_comment
    ON public.leave_comment_attachments(leave_comment_id, uploaded_at, id);

COMMIT;
