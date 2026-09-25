BEGIN;

ALTER TABLE public.pre_employees
    ADD COLUMN IF NOT EXISTS did_not_start_work BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS did_not_start_work_by VARCHAR(50),
    ADD COLUMN IF NOT EXISTS did_not_start_work_by_name VARCHAR(200),
    ADD COLUMN IF NOT EXISTS did_not_start_work_at TIMESTAMPTZ;

CREATE INDEX IF NOT EXISTS ix_pre_employees_active_queue
    ON public.pre_employees(status, created_at DESC)
    WHERE did_not_start_work = FALSE;

COMMIT;
