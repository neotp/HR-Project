CREATE TABLE IF NOT EXISTS public.leave_calendar_events
(
    id                  BIGSERIAL PRIMARY KEY,
    leave_document_id   BIGINT NOT NULL,
    employee_email      VARCHAR(320),
    outlook_event_id    TEXT,
    outlook_web_link    TEXT,
    transaction_id      UUID NOT NULL,
    sync_status         VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    last_action         VARCHAR(20),
    retry_count         INTEGER NOT NULL DEFAULT 0,
    last_sync_error     TEXT,
    last_attempted_at   TIMESTAMPTZ,
    synced_at           TIMESTAMPTZ,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_leave_calendar_events_document
        FOREIGN KEY (leave_document_id)
        REFERENCES public.leave_documents(id)
        ON DELETE CASCADE,
    CONSTRAINT ux_leave_calendar_events_document UNIQUE (leave_document_id),
    CONSTRAINT ux_leave_calendar_events_transaction UNIQUE (transaction_id),
    CONSTRAINT ck_leave_calendar_events_status
        CHECK (sync_status IN ('PENDING', 'SYNCED', 'FAILED', 'DELETED')),
    CONSTRAINT ck_leave_calendar_events_action
        CHECK (last_action IS NULL OR last_action IN ('CREATE', 'UPDATE', 'DELETE')),
    CONSTRAINT ck_leave_calendar_events_retry_count CHECK (retry_count >= 0)
);

CREATE INDEX IF NOT EXISTS ix_leave_calendar_events_retry
    ON public.leave_calendar_events(sync_status, last_attempted_at)
    WHERE sync_status IN ('PENDING', 'FAILED');
