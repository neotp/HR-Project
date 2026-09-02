BEGIN;

ALTER TABLE public.employee_company_info
    ADD COLUMN IF NOT EXISTS exclude_attendance_calculation BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON COLUMN public.employee_company_info.exclude_attendance_calculation IS
    'When true, Attendance Worker keeps raw scans but does not calculate attendance daily records for this employee.';

COMMIT;
