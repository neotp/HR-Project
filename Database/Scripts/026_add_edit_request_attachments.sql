BEGIN;

ALTER TABLE public.leave_document_attachments
    ADD COLUMN IF NOT EXISTS leave_edit_request_id BIGINT NULL;

ALTER TABLE public.leave_document_attachments
    DROP CONSTRAINT IF EXISTS fk_leave_attachments_edit_request;

ALTER TABLE public.leave_document_attachments
    ADD CONSTRAINT fk_leave_attachments_edit_request
        FOREIGN KEY (leave_edit_request_id)
        REFERENCES public.leave_edit_requests(id) ON DELETE CASCADE;

CREATE INDEX IF NOT EXISTS ix_leave_attachments_edit_request
    ON public.leave_document_attachments(leave_edit_request_id)
    WHERE leave_edit_request_id IS NOT NULL;

COMMIT;
