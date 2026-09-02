BEGIN;

CREATE TABLE IF NOT EXISTS public.attendance_responses
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    attendance_daily_id BIGINT       NOT NULL
        REFERENCES public.attendance_daily_records(id) ON DELETE CASCADE,
    response_text       TEXT         NOT NULL,
    status              VARCHAR(20)  NOT NULL DEFAULT 'SUBMITTED',
    submitted_by        VARCHAR(50)  NOT NULL,
    submitted_by_name   VARCHAR(200) NOT NULL,
    submitted_at        TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    reviewed_by         VARCHAR(50),
    reviewed_by_name    VARCHAR(200),
    reviewed_at         TIMESTAMPTZ,
    review_note         TEXT,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_attendance_response_status CHECK
        (status IN ('SUBMITTED','APPROVED','REJECTED','CANCELLED')),
    CONSTRAINT ck_attendance_response_text CHECK
        (length(btrim(response_text)) BETWEEN 1 AND 4000)
);

CREATE INDEX IF NOT EXISTS ix_attendance_responses_daily_time
    ON public.attendance_responses(attendance_daily_id, submitted_at DESC, id DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ux_attendance_response_pending
    ON public.attendance_responses(attendance_daily_id)
    WHERE status = 'SUBMITTED';

CREATE TABLE IF NOT EXISTS public.attendance_response_attachments
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    attendance_response_id BIGINT      NOT NULL
        REFERENCES public.attendance_responses(id) ON DELETE CASCADE,
    original_file_name VARCHAR(255) NOT NULL,
    content_type       VARCHAR(150) NOT NULL,
    file_size_bytes    BIGINT       NOT NULL,
    file_content       BYTEA        NOT NULL,
    uploaded_at        TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_attendance_response_attachment_size CHECK
        (file_size_bytes > 0 AND file_size_bytes <= 10485760),
    CONSTRAINT ck_attendance_response_attachment_content CHECK
        (octet_length(file_content) > 0 AND octet_length(file_content) <= 10485760)
);

CREATE INDEX IF NOT EXISTS ix_attendance_response_attachments_response
    ON public.attendance_response_attachments(attendance_response_id, uploaded_at, id);

ALTER TABLE public.attendance_daily_history
    DROP CONSTRAINT IF EXISTS ck_attendance_history_action;
ALTER TABLE public.attendance_daily_history
    ADD CONSTRAINT ck_attendance_history_action CHECK
    (
        action IN
        ('AUTO_CALCULATE','AUTO_RECALCULATE','MANUAL_OVERRIDE','RESET_OVERRIDE','RESPONSE_SUBMITTED')
    );

COMMIT;
