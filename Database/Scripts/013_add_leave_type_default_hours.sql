BEGIN;

ALTER TABLE public.leave_types
    ADD COLUMN IF NOT EXISTS default_hours NUMERIC(8,2) NOT NULL DEFAULT 0;

ALTER TABLE public.leave_types
    DROP CONSTRAINT IF EXISTS ck_leave_types_default_hours;

ALTER TABLE public.leave_types
    ADD CONSTRAINT ck_leave_types_default_hours
        CHECK (default_hours >= 0);

COMMENT ON COLUMN public.leave_types.default_hours IS
    'Default annual leave quota for this leave type, measured in hours.';

COMMIT;
