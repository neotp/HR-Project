BEGIN;

INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT page.id, 'EXPORT', 'Export ข้อมูลพนักงาน',
       'Export ข้อมูลพนักงานทุกฟิลด์และข้อมูลค่าลดหย่อนภาษี โดยไม่จำกัดตามสิทธิ์การมองเห็นแต่ละ Tab',
       40
FROM public.application_pages page
WHERE page.page_key = 'EMPLOYEES'
ON CONFLICT (application_page_id, action_key) DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

-- Do not seed employee or App Role grants. Export contains sensitive data and
-- stays disabled until an administrator explicitly enables this action.

COMMIT;
