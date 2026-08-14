BEGIN;

ALTER TABLE public.manager_leave_notifications
    ADD COLUMN IF NOT EXISTS leave_hours NUMERIC(8,2) NULL;

ALTER TABLE public.manager_leave_notifications
    DROP CONSTRAINT IF EXISTS ck_manager_leave_notification_hours;

ALTER TABLE public.manager_leave_notifications
    ADD CONSTRAINT ck_manager_leave_notification_hours
        CHECK (leave_hours IS NULL OR (leave_hours > 0 AND leave_hours <= 24));

COMMENT ON COLUMN public.manager_leave_notifications.leave_hours IS
    'จำนวนชั่วโมงลาที่หัวหน้าแจ้งให้ลูกน้องทราบ ไม่มีผลต่อเอกสารลาหรือโควตา';

COMMIT;
