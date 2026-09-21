BEGIN;

CREATE TABLE IF NOT EXISTS public.attendance_comments
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    attendance_daily_id BIGINT NOT NULL
        REFERENCES public.attendance_daily_records(id) ON DELETE CASCADE,
    comment_text        TEXT NOT NULL,
    commented_by        VARCHAR(50) NOT NULL,
    commented_by_name   VARCHAR(200) NOT NULL,
    commented_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT ck_attendance_comments_text
        CHECK (char_length(btrim(comment_text)) BETWEEN 1 AND 4000)
);

CREATE INDEX IF NOT EXISTS ix_attendance_comments_daily_time
    ON public.attendance_comments(attendance_daily_id, commented_at, id)
    WHERE is_active = TRUE;

COMMIT;
