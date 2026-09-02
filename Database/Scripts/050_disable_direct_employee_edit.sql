BEGIN;

UPDATE public.application_page_actions action
SET is_active = FALSE
FROM public.application_pages page
WHERE action.application_page_id = page.id
  AND page.page_key = 'EMPLOYEES'
  AND action.action_key = 'EDIT';

COMMIT;
