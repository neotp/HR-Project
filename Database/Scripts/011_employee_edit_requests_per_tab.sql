BEGIN;

-- เดิมจำกัดคำขอรออนุมัติไว้เพียงหนึ่งใบต่อพนักงาน ทำให้ส่งคนละ Tab พร้อมกันไม่ได้
DROP INDEX IF EXISTS public.ux_employee_edit_requests_pending;

CREATE INDEX IF NOT EXISTS ix_employee_edit_requests_pending_employee
    ON public.employee_edit_requests(employee_id, requested_at DESC)
    WHERE status = 'PENDING';

COMMIT;
