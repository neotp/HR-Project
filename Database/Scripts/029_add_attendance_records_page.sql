BEGIN;

UPDATE public.application_pages
SET page_name = 'ปฏิทินการมาทำงาน',
    route_path = '/attendance',
    category_name = 'การมาทำงาน',
    display_order = 82,
    is_active = TRUE
WHERE page_key = 'ATTENDANCE';

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order)
VALUES
    ('ATTENDANCE_RECORDS', 'การมาทำงาน', '/attendance/records', 'การมาทำงาน', 80)
ON CONFLICT (page_key)
DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

COMMIT;
