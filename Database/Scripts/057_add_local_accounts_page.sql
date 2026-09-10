BEGIN;

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order, is_active, is_enabled)
VALUES
    ('LOCAL_ACCOUNTS', 'จัดการบัญชี Local', '/local-accounts', 'การจัดการระบบ', 111, TRUE, TRUE)
ON CONFLICT (page_key)
DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order, is_active)
SELECT page.id, action.action_key, action.action_name, action.description, action.display_order, TRUE
FROM public.application_pages page
CROSS JOIN
(
    VALUES
        ('VIEW_ACCOUNTS', 'ดูบัญชี Local', 'ดูรายการและสถานะบัญชี Local ของพนักงาน', 10),
        ('CREATE_ACCOUNT', 'สร้างบัญชี Local', 'สร้างชื่อผู้ใช้และรหัสผ่าน Local ให้พนักงาน', 20),
        ('RESET_PASSWORD', 'รีเซ็ตรหัสผ่าน Local', 'กำหนดรหัสผ่านใหม่ให้บัญชี Local ที่มีอยู่', 30),
        ('ENABLE_DISABLE_ACCOUNT', 'เปิด/ปิดบัญชี Local', 'เปิดหรือระงับการเข้าสู่ระบบด้วยบัญชี Local', 40)
) AS action(action_key, action_name, description, display_order)
WHERE page.page_key = 'LOCAL_ACCOUNTS'
ON CONFLICT (application_page_id, action_key)
DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

-- ไม่กำหนดสิทธิ์เริ่มต้นให้พนักงานหรือ App Role เมนูจึงซ่อนจนกว่าจะได้รับสิทธิ์โดยตรง
COMMIT;
