BEGIN;

WITH action_seed(action_key, action_name, description, display_order) AS
(
    VALUES
    ('VIEW_ACCOUNTING', 'ดูข้อมูลทางบัญชี', 'ดูข้อมูลทางบัญชีของพนักงาน', 30),
    ('VIEW_WORK_HISTORY', 'ดูประวัติการทำงาน', 'ดูประวัติการทำงานของพนักงาน', 40),
    ('VIEW_EDUCATION', 'ดูประวัติการศึกษา', 'ดูประวัติการศึกษาของพนักงาน', 50),
    ('VIEW_TRAINING', 'ดูประวัติการอบรมและสัมมนา', 'ดูประวัติการอบรมและสัมมนาของพนักงาน', 60),
    ('VIEW_TAX_DEDUCTION', 'ดูข้อมูลค่าลดหย่อนภาษี', 'ดูข้อมูลค่าลดหย่อนภาษีของพนักงาน', 70),
    ('VIEW_RECRUIT_DOCUMENTS', 'ดูเอกสาร Recruit', 'ดูเอกสาร Recruit ของพนักงานแบบอ่านอย่างเดียว', 80),
    ('VIEW_CHANGE_HISTORY', 'ดูประวัติการเปลี่ยนแปลง', 'ดูประวัติการเปลี่ยนแปลงข้อมูลพนักงาน', 90),
    ('EDIT_INTERNAL', 'แก้ไขข้อมูลภายในบริษัท', 'แก้ไขข้อมูลภายในบริษัทได้โดยตรง', 110),
    ('EDIT_ACCOUNTING', 'แก้ไขข้อมูลทางบัญชี', 'แก้ไขข้อมูลทางบัญชีได้โดยตรง', 120),
    ('EDIT_WORK_HISTORY', 'แก้ไขประวัติการทำงาน', 'แก้ไขประวัติการทำงานได้โดยตรง', 130),
    ('EDIT_TRAINING', 'แก้ไขประวัติการอบรมและสัมมนา', 'แก้ไขประวัติการอบรมและสัมมนาได้โดยตรง', 140)
)
INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT page.id, seed.action_key, seed.action_name, seed.description, seed.display_order
FROM action_seed seed
JOIN public.application_pages page ON page.page_key = 'EMPLOYEES'
ON CONFLICT (application_page_id, action_key) DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

-- Preserve existing visibility while allowing administrators to separate it per tab afterwards.
WITH permission_map(source_key, target_key) AS
(
    VALUES
    ('VIEW_PERSONAL', 'VIEW_ACCOUNTING'),
    ('VIEW_PERSONAL', 'VIEW_EDUCATION'),
    ('VIEW_PERSONAL', 'VIEW_TRAINING'),
    ('VIEW_PERSONAL', 'VIEW_TAX_DEDUCTION'),
    ('VIEW_COMPANY', 'VIEW_WORK_HISTORY'),
    ('VIEW_PERSONAL_DOCUMENTS', 'VIEW_RECRUIT_DOCUMENTS'),
    ('VIEW_PERSONAL_DOCUMENTS', 'VIEW_CHANGE_HISTORY'),
    ('EDIT', 'EDIT_INTERNAL'),
    ('EDIT', 'EDIT_ACCOUNTING'),
    ('EDIT', 'EDIT_WORK_HISTORY'),
    ('EDIT', 'EDIT_TRAINING')
)
INSERT INTO public.employee_page_action_permissions
    (employee_id, application_page_action_id, can_execute, updated_by, updated_by_name)
SELECT permission.employee_id, target.id, permission.can_execute,
       permission.updated_by, permission.updated_by_name
FROM public.employee_page_action_permissions permission
JOIN public.application_page_actions source ON source.id = permission.application_page_action_id
JOIN public.application_pages page ON page.id = source.application_page_id AND page.page_key = 'EMPLOYEES'
JOIN permission_map map ON map.source_key = source.action_key
JOIN public.application_page_actions target
  ON target.application_page_id = page.id AND target.action_key = map.target_key
ON CONFLICT (employee_id, application_page_action_id) DO NOTHING;

WITH permission_map(source_key, target_key) AS
(
    VALUES
    ('VIEW_PERSONAL', 'VIEW_ACCOUNTING'),
    ('VIEW_PERSONAL', 'VIEW_EDUCATION'),
    ('VIEW_PERSONAL', 'VIEW_TRAINING'),
    ('VIEW_PERSONAL', 'VIEW_TAX_DEDUCTION'),
    ('VIEW_COMPANY', 'VIEW_WORK_HISTORY'),
    ('VIEW_PERSONAL_DOCUMENTS', 'VIEW_RECRUIT_DOCUMENTS'),
    ('VIEW_PERSONAL_DOCUMENTS', 'VIEW_CHANGE_HISTORY'),
    ('EDIT', 'EDIT_INTERNAL'),
    ('EDIT', 'EDIT_ACCOUNTING'),
    ('EDIT', 'EDIT_WORK_HISTORY'),
    ('EDIT', 'EDIT_TRAINING')
)
INSERT INTO public.app_role_page_action_permissions
    (app_role_id, application_page_action_id, can_execute, updated_by, updated_by_name)
SELECT permission.app_role_id, target.id, permission.can_execute,
       permission.updated_by, permission.updated_by_name
FROM public.app_role_page_action_permissions permission
JOIN public.application_page_actions source ON source.id = permission.application_page_action_id
JOIN public.application_pages page ON page.id = source.application_page_id AND page.page_key = 'EMPLOYEES'
JOIN permission_map map ON map.source_key = source.action_key
JOIN public.application_page_actions target
  ON target.application_page_id = page.id AND target.action_key = map.target_key
ON CONFLICT (app_role_id, application_page_action_id) DO NOTHING;

UPDATE public.application_page_actions action
SET action_name = 'ดูเอกสารส่วนตัว',
    description = 'ดูและเปิดพรีวิวเอกสารส่วนตัวของพนักงาน'
FROM public.application_pages page
WHERE action.application_page_id = page.id
  AND page.page_key = 'EMPLOYEES'
  AND action.action_key = 'VIEW_PERSONAL_DOCUMENTS';

COMMIT;
