BEGIN;

ALTER TABLE public.employee_personal_documents
    ADD COLUMN IF NOT EXISTS deleted_by VARCHAR(50),
    ADD COLUMN IF NOT EXISTS deleted_by_name VARCHAR(200),
    ADD COLUMN IF NOT EXISTS deleted_at TIMESTAMPTZ;

CREATE TABLE IF NOT EXISTS public.employee_activity_history
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id     BIGINT NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    action_key      VARCHAR(80) NOT NULL,
    details_text    TEXT NOT NULL,
    entity_type     VARCHAR(80),
    entity_id       BIGINT,
    action_by       VARCHAR(50) NOT NULL,
    action_by_name  VARCHAR(200) NOT NULL,
    action_at       TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_employee_activity_history_employee
    ON public.employee_activity_history (employee_id, action_at DESC, id DESC);

-- Preserve documents uploaded before activity auditing was introduced.
INSERT INTO public.employee_activity_history
    (employee_id, action_key, details_text, entity_type, entity_id,
     action_by, action_by_name, action_at)
SELECT document.employee_id,
       'PERSONAL_DOCUMENT_ADDED',
       CONCAT('เพิ่มเอกสาร ', document.original_file_name,
              ' ขนาด ', document.file_size_bytes, ' ไบต์'),
       'PERSONAL_DOCUMENT',
       document.id,
       document.uploaded_by,
       document.uploaded_by_name,
       document.uploaded_at
FROM public.employee_personal_documents document
WHERE NOT EXISTS
(
    SELECT 1
    FROM public.employee_activity_history history
    WHERE history.entity_type = 'PERSONAL_DOCUMENT'
      AND history.entity_id = document.id
      AND history.action_key = 'PERSONAL_DOCUMENT_ADDED'
);

UPDATE public.application_page_actions action
SET action_name = 'ดู เพิ่ม และลบเอกสารส่วนตัว',
    description = 'ดู เพิ่ม เปิดพรีวิว และลบเอกสารส่วนตัวของพนักงาน',
    is_active = TRUE
FROM public.application_pages page
WHERE page.id = action.application_page_id
  AND page.page_key = 'EMPLOYEES'
  AND action.action_key = 'VIEW_PERSONAL_DOCUMENTS';

COMMIT;
