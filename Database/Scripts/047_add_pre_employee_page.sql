BEGIN;

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order)
VALUES
    ('PRE_EMPLOYEES', 'Pre-Employee', '/recruitment/pre-employees', 'การรับเข้าทำงาน', 90)
ON CONFLICT (page_key)
DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

COMMIT;
