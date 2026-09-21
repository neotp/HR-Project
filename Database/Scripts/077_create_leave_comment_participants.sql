BEGIN;

CREATE TABLE IF NOT EXISTS public.leave_comment_participants
(
    id                    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_document_id     BIGINT NOT NULL REFERENCES public.leave_documents(id) ON DELETE CASCADE,
    employee_id           VARCHAR(50) NOT NULL,
    employee_name         VARCHAR(300) NOT NULL,
    employee_email        VARCHAR(320) NOT NULL,
    added_by              VARCHAR(50) NOT NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT uq_leave_comment_participant UNIQUE (leave_document_id, employee_id)
);

CREATE INDEX IF NOT EXISTS ix_leave_comment_participants_document
    ON public.leave_comment_participants(leave_document_id, employee_name, employee_id);

COMMIT;
