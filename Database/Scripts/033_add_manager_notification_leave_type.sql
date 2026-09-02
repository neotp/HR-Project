BEGIN;

ALTER TABLE public.manager_leave_notifications
    ADD COLUMN IF NOT EXISTS leave_type_id BIGINT NULL;

DO $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_manager_leave_notifications_leave_type'
          AND conrelid = 'public.manager_leave_notifications'::regclass
    ) THEN
        ALTER TABLE public.manager_leave_notifications
            ADD CONSTRAINT fk_manager_leave_notifications_leave_type
            FOREIGN KEY (leave_type_id)
            REFERENCES public.leave_types(id);
    END IF;
END;
$$;

CREATE INDEX IF NOT EXISTS ix_manager_leave_notifications_leave_type
    ON public.manager_leave_notifications(leave_type_id);

COMMENT ON COLUMN public.manager_leave_notifications.leave_type_id IS
    'ประเภทการลาที่หัวหน้าเลือกเพื่อใช้แจ้งข้อมูลเท่านั้น ไม่มีผลต่อใบลาหรือโควตา';

COMMIT;
