BEGIN;

ALTER TABLE public.leave_quota_requests
    ADD COLUMN IF NOT EXISTS approved_hours NUMERIC(8,2);

ALTER TABLE public.leave_quota_requests
    DROP CONSTRAINT IF EXISTS ck_leave_quota_requests_approved_hours;

ALTER TABLE public.leave_quota_requests
    ADD CONSTRAINT ck_leave_quota_requests_approved_hours
    CHECK
    (
        approved_hours IS NULL
        OR (approved_hours >= 0 AND approved_hours <= requested_hours)
    );

COMMIT;
