BEGIN;

ALTER TABLE public.attendance_daily_records
    ADD COLUMN IF NOT EXISTS calculated_late_minutes INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS calculated_missing_minutes INTEGER NOT NULL DEFAULT 0;

UPDATE public.attendance_daily_records
SET calculated_late_minutes = late_minutes,
    calculated_missing_minutes = missing_minutes
WHERE calculated_late_minutes = 0
  AND calculated_missing_minutes = 0
  AND (late_minutes > 0 OR missing_minutes > 0);

ALTER TABLE public.attendance_daily_records
    DROP CONSTRAINT IF EXISTS ck_attendance_daily_calculated_counts;
ALTER TABLE public.attendance_daily_records
    ADD CONSTRAINT ck_attendance_daily_calculated_counts CHECK
        (calculated_late_minutes >= 0 AND calculated_missing_minutes >= 0);

ALTER TABLE public.attendance_daily_history
    DROP CONSTRAINT IF EXISTS ck_attendance_history_action;
ALTER TABLE public.attendance_daily_history
    ADD CONSTRAINT ck_attendance_history_action CHECK
    (
        action IN
        ('AUTO_CALCULATE','AUTO_RECALCULATE','MANUAL_OVERRIDE','RESET_OVERRIDE',
         'RESPONSE_SUBMITTED','RESPONSE_APPROVED','RESPONSE_REJECTED')
    );

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order)
VALUES
    ('ATTENDANCE_REVIEWS', 'ตรวจสอบข้อโต้แย้งการมาทำงาน', '/attendance/reviews', 'การมาทำงาน', 81)
ON CONFLICT (page_key) DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

WITH action_seed(action_key, action_name, description, display_order) AS
(
    VALUES
        ('APPROVE', 'อนุมัติข้อโต้แย้ง', 'เปลี่ยนผลการมาทำงานเป็นมาปกติและเก็บผลคำนวณเดิมไว้', 10),
        ('REJECT', 'ไม่อนุมัติข้อโต้แย้ง', 'คงผลขาดงานหรือมาสายเดิมไว้', 20)
)
INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT page.id, seed.action_key, seed.action_name, seed.description, seed.display_order
FROM action_seed seed
JOIN public.application_pages page ON page.page_key = 'ATTENDANCE_REVIEWS'
ON CONFLICT (application_page_id, action_key) DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

COMMIT;
