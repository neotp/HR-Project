BEGIN;

ALTER TABLE public.attendance_responses
    DROP CONSTRAINT IF EXISTS ck_attendance_response_status;
ALTER TABLE public.attendance_responses
    ADD CONSTRAINT ck_attendance_response_status CHECK
        (status IN ('SUBMITTED','APPROVED','REJECTED','PARTIALLY_APPROVED','CANCELLED'));

CREATE TABLE IF NOT EXISTS public.attendance_response_issues
(
    id                     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    attendance_response_id BIGINT NOT NULL
        REFERENCES public.attendance_responses(id) ON DELETE CASCADE,
    issue_type             VARCHAR(20) NOT NULL,
    status                 VARCHAR(20) NOT NULL DEFAULT 'SUBMITTED',
    reviewed_by            VARCHAR(50),
    reviewed_by_name       VARCHAR(200),
    reviewed_at            TIMESTAMPTZ,
    review_note            TEXT,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at             TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_attendance_response_issue UNIQUE(attendance_response_id, issue_type),
    CONSTRAINT ck_attendance_response_issue_type CHECK(issue_type IN ('LATE','ABSENT')),
    CONSTRAINT ck_attendance_response_issue_status CHECK(status IN ('SUBMITTED','APPROVED','REJECTED'))
);

CREATE INDEX IF NOT EXISTS ix_attendance_response_issues_response_status
    ON public.attendance_response_issues(attendance_response_id, status);

INSERT INTO public.attendance_response_issues
    (attendance_response_id, issue_type, status, reviewed_by, reviewed_by_name, reviewed_at, review_note)
SELECT response.id, issue.issue_type,
       CASE response.status
           WHEN 'APPROVED' THEN 'APPROVED'
           WHEN 'REJECTED' THEN 'REJECTED'
           ELSE 'SUBMITTED'
       END,
       response.reviewed_by, response.reviewed_by_name, response.reviewed_at, response.review_note
FROM public.attendance_responses response
JOIN public.attendance_daily_records daily ON daily.id=response.attendance_daily_id
CROSS JOIN LATERAL
(
    SELECT 'LATE'::varchar AS issue_type
    WHERE daily.calculated_late_minutes > 0 OR daily.calculated_status='LATE'
    UNION ALL
    SELECT 'ABSENT'::varchar
    WHERE daily.calculated_missing_minutes > 0 OR daily.calculated_status='ABSENT'
) issue
ON CONFLICT (attendance_response_id, issue_type) DO NOTHING;

COMMIT;
