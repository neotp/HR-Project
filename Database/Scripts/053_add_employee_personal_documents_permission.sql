BEGIN;

INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order, is_active)
SELECT page.id,
       'VIEW_PERSONAL_DOCUMENTS',
       'ดูและเพิ่มเอกสารส่วนตัว',
       'ดู เพิ่ม และเปิดพรีวิวเอกสารส่วนตัวของพนักงาน',
       25,
       TRUE
FROM public.application_pages page
WHERE page.page_key = 'EMPLOYEES'
ON CONFLICT (application_page_id, action_key)
DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

-- No employee or App Role permission is seeded intentionally.
-- Access must be granted explicitly from the permission management page.

COMMIT;
