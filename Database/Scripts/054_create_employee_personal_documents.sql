BEGIN;

CREATE TABLE IF NOT EXISTS public.employee_personal_documents
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id         BIGINT NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    original_file_name  VARCHAR(255) NOT NULL,
    content_type        VARCHAR(100) NOT NULL,
    file_size_bytes     BIGINT NOT NULL,
    file_content        BYTEA NOT NULL,
    uploaded_by         VARCHAR(50) NOT NULL,
    uploaded_by_name    VARCHAR(200) NOT NULL,
    uploaded_at         TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    CONSTRAINT ck_employee_personal_documents_size
        CHECK (file_size_bytes > 0 AND file_size_bytes <= 10485760),
    CONSTRAINT ck_employee_personal_documents_content_size
        CHECK (octet_length(file_content) = file_size_bytes)
);

CREATE INDEX IF NOT EXISTS ix_employee_personal_documents_employee
    ON public.employee_personal_documents (employee_id, uploaded_at DESC, id DESC)
    WHERE is_active = TRUE;

UPDATE public.application_page_actions action
SET action_name = 'ดูและเพิ่มเอกสารส่วนตัว',
    description = 'ดู เพิ่ม และเปิดพรีวิวเอกสารส่วนตัวของพนักงาน',
    is_active = TRUE
FROM public.application_pages page
WHERE page.id = action.application_page_id
  AND page.page_key = 'EMPLOYEES'
  AND action.action_key = 'VIEW_PERSONAL_DOCUMENTS';

COMMIT;
