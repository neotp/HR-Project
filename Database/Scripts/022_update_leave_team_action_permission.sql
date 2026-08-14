UPDATE public.application_page_actions action
SET action_name = 'แจ้งข้อมูลให้พนักงานทุกคน',
    description = 'ส่งอีเมลแจ้งข้อมูลให้พนักงานนอกเหนือจาก Boss หรือ Reporting To ของตนเอง',
    display_order = 10,
    is_active = TRUE,
    updated_at = CURRENT_TIMESTAMP
FROM public.application_pages page
WHERE page.id = action.application_page_id
  AND page.page_key = 'LEAVE_TEAM'
  AND action.action_key = 'CREATE_FOR_OTHERS';

INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT page.id, 'CREATE_FOR_OTHERS', 'แจ้งข้อมูลให้พนักงานทุกคน',
       'ส่งอีเมลแจ้งข้อมูลให้พนักงานนอกเหนือจาก Boss หรือ Reporting To ของตนเอง', 10
FROM public.application_pages page
WHERE page.page_key = 'LEAVE_TEAM'
  AND NOT EXISTS
  (
      SELECT 1 FROM public.application_page_actions action
      WHERE action.application_page_id = page.id
        AND action.action_key = 'CREATE_FOR_OTHERS'
  );
