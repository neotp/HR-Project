BEGIN;

CREATE TABLE IF NOT EXISTS public.leave_quota_request_comments
(
    id                       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_quota_request_id   BIGINT NOT NULL
        REFERENCES public.leave_quota_requests(id) ON DELETE CASCADE,
    comment_text             TEXT NOT NULL,
    commented_by             VARCHAR(50) NOT NULL,
    commented_by_name        VARCHAR(200) NOT NULL,
    commented_at             TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_active                BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT ck_leave_quota_request_comments_text
        CHECK (char_length(btrim(comment_text)) BETWEEN 1 AND 4000)
);

CREATE INDEX IF NOT EXISTS ix_leave_quota_request_comments_request_time
    ON public.leave_quota_request_comments
        (leave_quota_request_id, commented_at, id)
    WHERE is_active = TRUE;

COMMIT;
