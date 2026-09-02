BEGIN;

CREATE TABLE IF NOT EXISTS public.leave_cancel_requests
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_document_id   BIGINT       NOT NULL,
    request_reason      TEXT         NOT NULL,
    status              VARCHAR(30)  NOT NULL DEFAULT 'PENDING',
    requested_by        VARCHAR(50)  NOT NULL,
    requested_by_name   VARCHAR(200) NOT NULL,
    requested_at        TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    reviewed_by         VARCHAR(50),
    reviewed_by_name    VARCHAR(200),
    reviewed_at         TIMESTAMPTZ,
    review_remark       TEXT,
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_leave_cancel_requests_document
        FOREIGN KEY (leave_document_id)
        REFERENCES public.leave_documents(id) ON DELETE CASCADE,
    CONSTRAINT ck_leave_cancel_requests_status
        CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_leave_cancel_requests_pending
    ON public.leave_cancel_requests(leave_document_id)
    WHERE status = 'PENDING';

CREATE INDEX IF NOT EXISTS ix_leave_cancel_requests_document_status
    ON public.leave_cancel_requests(leave_document_id, status);

ALTER TABLE public.leave_document_history
    DROP CONSTRAINT IF EXISTS ck_leave_history_action;

ALTER TABLE public.leave_document_history
    ADD CONSTRAINT ck_leave_history_action
    CHECK (action IN
    (
        'CREATE_DOCUMENT',
        'APPROVE',
        'REJECT',
        'EDIT',
        'CANCEL',
        'REQUEST_EDIT',
        'CANCEL_EDIT_REQUEST',
        'APPROVE_EDIT_REQUEST',
        'REQUEST_CANCEL',
        'CANCEL_CANCEL_REQUEST',
        'APPROVE_CANCEL_REQUEST',
        'REJECT_CANCEL_REQUEST'
    ));

DROP TRIGGER IF EXISTS trg_leave_cancel_requests_updated_at ON public.leave_cancel_requests;
CREATE TRIGGER trg_leave_cancel_requests_updated_at
BEFORE UPDATE ON public.leave_cancel_requests
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

COMMIT;
