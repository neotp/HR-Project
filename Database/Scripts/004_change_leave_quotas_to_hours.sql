BEGIN;

ALTER TABLE public.leave_quotas
    ADD COLUMN IF NOT EXISTS quota_hours NUMERIC(8,2),
    ADD COLUMN IF NOT EXISTS used_hours NUMERIC(8,2);

-- Preserve existing quota data using the project's current 8-hour workday.
DO $$
BEGIN
    IF EXISTS
    (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'leave_quotas'
          AND column_name = 'allocated_days'
    )
    THEN
        EXECUTE '
            UPDATE public.leave_quotas
            SET quota_hours = (allocated_days + carried_forward_days) * 8,
                used_hours = used_days * 8
            WHERE quota_hours IS NULL OR used_hours IS NULL
        ';
    END IF;
END;
$$;

ALTER TABLE public.leave_quotas
    ALTER COLUMN quota_hours SET DEFAULT 0,
    ALTER COLUMN quota_hours SET NOT NULL,
    ALTER COLUMN used_hours SET DEFAULT 0,
    ALTER COLUMN used_hours SET NOT NULL;

ALTER TABLE public.leave_quotas
    DROP CONSTRAINT IF EXISTS ck_leave_quotas_days;

ALTER TABLE public.leave_quotas
    DROP CONSTRAINT IF EXISTS ck_leave_quotas_hours;

ALTER TABLE public.leave_quotas
    ADD CONSTRAINT ck_leave_quotas_hours
    CHECK
    (
        quota_hours >= 0
        AND used_hours >= 0
        AND used_hours <= quota_hours
    );

ALTER TABLE public.leave_quotas
    DROP COLUMN IF EXISTS allocated_days,
    DROP COLUMN IF EXISTS carried_forward_days,
    DROP COLUMN IF EXISTS used_days;

COMMIT;
