BEGIN;

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order)
VALUES
    ('LEAVE_ALL_DOCUMENTS', 'เอกสารทั้งหมด', '/leave/all-documents', 'การลา', 15)
ON CONFLICT (page_key)
DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT id, 'VIEW_ALL', 'ดูเอกสารทั้งหมด',
       'ดูเอกสารการลาของพนักงานทุกคนโดยไม่รวมไฟล์แนบ', 10
FROM public.application_pages
WHERE page_key = 'LEAVE_ALL_DOCUMENTS'
ON CONFLICT (application_page_id, action_key)
DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

-- Preserve access for employees and application roles that already had the
-- legacy LEAVE_DOCUMENTS / VIEW_ALL permission before this page was separated.
INSERT INTO public.employee_page_permissions
    (employee_id, application_page_id, can_access, updated_by, updated_by_name)
SELECT permission.employee_id, new_page.id, permission.can_access,
       permission.updated_by, permission.updated_by_name
FROM public.employee_page_permissions permission
JOIN public.application_pages old_page
  ON old_page.id = permission.application_page_id
 AND old_page.page_key = 'LEAVE_DOCUMENTS'
CROSS JOIN public.application_pages new_page
WHERE new_page.page_key = 'LEAVE_ALL_DOCUMENTS'
ON CONFLICT (employee_id, application_page_id) DO NOTHING;

INSERT INTO public.employee_page_action_permissions
    (employee_id, application_page_action_id, can_execute, updated_by, updated_by_name)
SELECT permission.employee_id, new_action.id, permission.can_execute,
       permission.updated_by, permission.updated_by_name
FROM public.employee_page_action_permissions permission
JOIN public.application_page_actions old_action
  ON old_action.id = permission.application_page_action_id
 AND old_action.action_key = 'VIEW_ALL'
JOIN public.application_pages old_page
  ON old_page.id = old_action.application_page_id
 AND old_page.page_key = 'LEAVE_DOCUMENTS'
CROSS JOIN public.application_page_actions new_action
JOIN public.application_pages new_page
  ON new_page.id = new_action.application_page_id
 AND new_page.page_key = 'LEAVE_ALL_DOCUMENTS'
WHERE new_action.action_key = 'VIEW_ALL'
ON CONFLICT (employee_id, application_page_action_id) DO NOTHING;

INSERT INTO public.app_role_page_permissions
    (app_role_id, application_page_id, can_access, updated_by, updated_by_name)
SELECT permission.app_role_id, new_page.id, permission.can_access,
       permission.updated_by, permission.updated_by_name
FROM public.app_role_page_permissions permission
JOIN public.application_pages old_page
  ON old_page.id = permission.application_page_id
 AND old_page.page_key = 'LEAVE_DOCUMENTS'
CROSS JOIN public.application_pages new_page
WHERE new_page.page_key = 'LEAVE_ALL_DOCUMENTS'
ON CONFLICT (app_role_id, application_page_id) DO NOTHING;

INSERT INTO public.app_role_page_action_permissions
    (app_role_id, application_page_action_id, can_execute, updated_by, updated_by_name)
SELECT permission.app_role_id, new_action.id, permission.can_execute,
       permission.updated_by, permission.updated_by_name
FROM public.app_role_page_action_permissions permission
JOIN public.application_page_actions old_action
  ON old_action.id = permission.application_page_action_id
 AND old_action.action_key = 'VIEW_ALL'
JOIN public.application_pages old_page
  ON old_page.id = old_action.application_page_id
 AND old_page.page_key = 'LEAVE_DOCUMENTS'
CROSS JOIN public.application_page_actions new_action
JOIN public.application_pages new_page
  ON new_page.id = new_action.application_page_id
 AND new_page.page_key = 'LEAVE_ALL_DOCUMENTS'
WHERE new_action.action_key = 'VIEW_ALL'
ON CONFLICT (app_role_id, application_page_action_id) DO NOTHING;

COMMIT;
