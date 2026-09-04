BEGIN;

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order, is_active, is_enabled)
VALUES
    ('EMPLOYEES_RESIGNED', 'พนักงานที่ลาออก', '/employees/resigned', 'พนักงาน', 72, TRUE, TRUE)
ON CONFLICT (page_key)
DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

-- Intentionally do not copy permissions from EMPLOYEES.
-- The page remains hidden until access is explicitly granted to a person or App Role.

COMMIT;
