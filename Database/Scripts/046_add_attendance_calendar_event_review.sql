BEGIN;

ALTER TABLE public.attendance_calendar_events
    ADD COLUMN IF NOT EXISTS status VARCHAR(30) NOT NULL DEFAULT 'APPROVED',
    ADD COLUMN IF NOT EXISTS created_by VARCHAR(50),
    ADD COLUMN IF NOT EXISTS created_by_name VARCHAR(200),
    ADD COLUMN IF NOT EXISTS reviewed_by VARCHAR(50),
    ADD COLUMN IF NOT EXISTS reviewed_by_name VARCHAR(200),
    ADD COLUMN IF NOT EXISTS reviewed_at TIMESTAMPTZ,
    ADD COLUMN IF NOT EXISTS review_note TEXT;

UPDATE public.attendance_calendar_events event
SET created_by = COALESCE(event.created_by, event.employee_id),
    created_by_name = COALESCE(event.created_by_name,
        NULLIF(basic.full_name_th, ''), NULLIF(basic.full_name_en, ''), event.employee_id)
FROM public.employees employee
LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
WHERE employee.employee_code = event.employee_id
  AND (event.created_by IS NULL OR event.created_by_name IS NULL);

ALTER TABLE public.attendance_calendar_events
    DROP CONSTRAINT IF EXISTS ck_attendance_calendar_event_status;
ALTER TABLE public.attendance_calendar_events
    ADD CONSTRAINT ck_attendance_calendar_event_status
        CHECK (status IN ('PENDING_REVIEW', 'APPROVED', 'REJECTED'));

CREATE INDEX IF NOT EXISTS ix_attendance_calendar_events_review_status
    ON public.attendance_calendar_events(status, event_date, created_at DESC);

COMMIT;
