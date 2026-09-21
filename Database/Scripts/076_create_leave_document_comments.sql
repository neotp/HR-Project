BEGIN;

CREATE TABLE IF NOT EXISTS public.leave_document_comments
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_document_id   BIGINT NOT NULL REFERENCES public.leave_documents(id) ON DELETE CASCADE,
    author_employee_id  VARCHAR(50) NOT NULL,
    author_name         VARCHAR(300) NOT NULL,
    author_email        VARCHAR(320),
    comment_text        TEXT NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_leave_document_comment_text
        CHECK (LENGTH(BTRIM(comment_text)) BETWEEN 1 AND 4000)
);

CREATE INDEX IF NOT EXISTS ix_leave_document_comments_document
    ON public.leave_document_comments(leave_document_id, created_at, id);

CREATE TABLE IF NOT EXISTS public.leave_comment_notifications
(
    id                    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_comment_id      BIGINT NOT NULL REFERENCES public.leave_document_comments(id) ON DELETE CASCADE,
    recipient_employee_id VARCHAR(50),
    recipient_name        VARCHAR(300) NOT NULL,
    recipient_email       VARCHAR(320) NOT NULL,
    recipient_type        VARCHAR(20) NOT NULL,
    delivery_status       VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    retry_count           INTEGER NOT NULL DEFAULT 0,
    last_error            TEXT,
    last_attempted_at     TIMESTAMPTZ,
    sent_at               TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT uq_leave_comment_notification UNIQUE (leave_comment_id, recipient_email),
    CONSTRAINT ck_leave_comment_recipient_type
        CHECK (recipient_type IN ('REQUESTER', 'APPROVER', 'BOSS', 'LEAVE_APPROVER', 'WORKFLOW', 'ADDITIONAL')),
    CONSTRAINT ck_leave_comment_delivery_status
        CHECK (delivery_status IN ('PENDING', 'SENT', 'FAILED')),
    CONSTRAINT ck_leave_comment_notification_retry CHECK (retry_count >= 0)
);

CREATE INDEX IF NOT EXISTS ix_leave_comment_notifications_retry
    ON public.leave_comment_notifications(delivery_status, last_attempted_at, leave_comment_id)
    WHERE delivery_status IN ('PENDING', 'FAILED');

COMMIT;
