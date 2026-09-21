BEGIN;

INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT page.id,
       'EMAIL_NOTIFICATION',
       'รับอีเมลแจ้งเตือนข้อโต้แย้ง',
       'รับอีเมลเมื่อพนักงานส่งข้อโต้แย้งการมาทำงานรายการใหม่',
       30
FROM public.application_pages page
WHERE page.page_key = 'ATTENDANCE_REVIEWS'
ON CONFLICT (application_page_id, action_key) DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

COMMIT;
