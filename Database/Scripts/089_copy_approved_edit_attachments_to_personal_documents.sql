BEGIN;

ALTER TABLE public.employee_personal_documents
    ADD COLUMN IF NOT EXISTS source_type VARCHAR(50),
    ADD COLUMN IF NOT EXISTS source_id BIGINT,
    ADD COLUMN IF NOT EXISTS source_attachment_id BIGINT;

CREATE UNIQUE INDEX IF NOT EXISTS ux_employee_personal_documents_source
    ON public.employee_personal_documents(source_type, source_id, source_attachment_id)
    WHERE source_type IS NOT NULL
      AND source_id IS NOT NULL
      AND source_attachment_id IS NOT NULL;

WITH inserted AS
(
    INSERT INTO public.employee_personal_documents
        (employee_id, original_file_name, content_type, file_size_bytes,
         file_content, uploaded_by, uploaded_by_name,
         source_type, source_id, source_attachment_id)
    SELECT employee.id, attachment.file_name,
           LEFT(attachment.content_type, 100), attachment.file_size_bytes,
           attachment.file_content, request.requested_by, request.requested_by_name,
           'EMPLOYEE_EDIT_REQUEST', request.id, attachment.id
    FROM public.employee_edit_request_attachments attachment
    JOIN public.employee_edit_requests request
      ON request.id = attachment.employee_edit_request_id
    JOIN public.employees employee
      ON UPPER(BTRIM(employee.employee_code)) = UPPER(BTRIM(request.employee_id))
    WHERE request.status = 'APPROVED'
    ON CONFLICT (source_type, source_id, source_attachment_id)
        WHERE source_type IS NOT NULL
          AND source_id IS NOT NULL
          AND source_attachment_id IS NOT NULL
    DO NOTHING
    RETURNING id, employee_id, original_file_name, file_size_bytes,
              uploaded_by, uploaded_by_name, source_id
)
INSERT INTO public.employee_activity_history
    (employee_id, action_key, details_text, entity_type, entity_id,
     action_by, action_by_name)
SELECT inserted.employee_id, 'PERSONAL_DOCUMENT_ADDED',
       CONCAT('เพิ่มเอกสาร ', inserted.original_file_name,
              ' จากคำขอแก้ไข #', inserted.source_id,
              ' ขนาด ', inserted.file_size_bytes, ' ไบต์'),
       'PERSONAL_DOCUMENT', inserted.id,
       inserted.uploaded_by, inserted.uploaded_by_name
FROM inserted;

COMMIT;
