BEGIN;

INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT page.id,
       'REQUEST_EDIT',
       'ส่งคำขอแก้ไขข้อมูลพนักงาน',
       'ส่งคำขอแก้ไขข้อมูลแทนพนักงานคนอื่น โดยข้อมูลจะเปลี่ยนเมื่อคำขอได้รับอนุมัติ',
       40
FROM public.application_pages page
WHERE page.page_key = 'EMPLOYEES'
ON CONFLICT (application_page_id, action_key)
DO UPDATE SET action_name = EXCLUDED.action_name,
              description = EXCLUDED.description,
              display_order = EXCLUDED.display_order,
              is_active = TRUE;

-- Preserve the behavior of roles that were already configured to manage all
-- employee/edit-request actions before this more specific permission existed.
INSERT INTO public.app_role_page_action_permissions
    (app_role_id, application_page_action_id, can_execute, updated_by, updated_by_name)
SELECT DISTINCT permission.app_role_id, request_action.id, TRUE,
       permission.updated_by, permission.updated_by_name
FROM public.app_role_page_action_permissions permission
JOIN public.application_page_actions source_action
  ON source_action.id = permission.application_page_action_id
JOIN public.application_pages source_page
  ON source_page.id = source_action.application_page_id
CROSS JOIN LATERAL
(
    SELECT action.id
    FROM public.application_page_actions action
    JOIN public.application_pages page ON page.id = action.application_page_id
    WHERE page.page_key = 'EMPLOYEES' AND action.action_key = 'REQUEST_EDIT'
    LIMIT 1
) request_action
WHERE permission.can_execute = TRUE
  AND
  (
      (source_page.page_key = 'EMPLOYEES'
       AND source_action.action_key IN ('VIEW_PERSONAL', 'VIEW_COMPANY'))
      OR (source_page.page_key = 'EMPLOYEE_EDIT_REQUESTS'
          AND source_action.action_key = 'VIEW_ALL')
  )
ON CONFLICT (app_role_id, application_page_action_id)
DO UPDATE SET can_execute = TRUE,
              updated_by = EXCLUDED.updated_by,
              updated_by_name = EXCLUDED.updated_by_name;

INSERT INTO public.employee_page_action_permissions
    (employee_id, application_page_action_id, can_execute, updated_by, updated_by_name)
SELECT DISTINCT permission.employee_id, request_action.id, TRUE,
       permission.updated_by, permission.updated_by_name
FROM public.employee_page_action_permissions permission
JOIN public.application_page_actions source_action
  ON source_action.id = permission.application_page_action_id
JOIN public.application_pages source_page
  ON source_page.id = source_action.application_page_id
CROSS JOIN LATERAL
(
    SELECT action.id
    FROM public.application_page_actions action
    JOIN public.application_pages page ON page.id = action.application_page_id
    WHERE page.page_key = 'EMPLOYEES' AND action.action_key = 'REQUEST_EDIT'
    LIMIT 1
) request_action
WHERE permission.can_execute = TRUE
  AND
  (
      (source_page.page_key = 'EMPLOYEES'
       AND source_action.action_key IN ('VIEW_PERSONAL', 'VIEW_COMPANY'))
      OR (source_page.page_key = 'EMPLOYEE_EDIT_REQUESTS'
          AND source_action.action_key = 'VIEW_ALL')
  )
ON CONFLICT (employee_id, application_page_action_id)
DO UPDATE SET can_execute = TRUE,
              updated_by = EXCLUDED.updated_by,
              updated_by_name = EXCLUDED.updated_by_name;

COMMIT;
