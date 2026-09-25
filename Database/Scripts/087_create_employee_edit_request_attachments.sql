BEGIN;

CREATE TABLE IF NOT EXISTS public.employee_edit_request_attachments
(
    id                          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_edit_request_id    BIGINT       NOT NULL,
    file_name                   VARCHAR(255) NOT NULL,
    content_type                VARCHAR(150) NOT NULL,
    file_size_bytes             BIGINT       NOT NULL,
    file_content                BYTEA        NOT NULL,
    uploaded_by                 VARCHAR(50)  NOT NULL,
    uploaded_at                 TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_employee_edit_attachment_request
        FOREIGN KEY (employee_edit_request_id)
        REFERENCES public.employee_edit_requests(id) ON DELETE CASCADE,
    CONSTRAINT ck_employee_edit_attachment_size
        CHECK (file_size_bytes > 0 AND file_size_bytes <= 3145728),
    CONSTRAINT ck_employee_edit_attachment_content
        CHECK (octet_length(file_content) = file_size_bytes)
);

CREATE INDEX IF NOT EXISTS ix_employee_edit_attachment_request
    ON public.employee_edit_request_attachments(employee_edit_request_id, uploaded_at, id);

COMMIT;
