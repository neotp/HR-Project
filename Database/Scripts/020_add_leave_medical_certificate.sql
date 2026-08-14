BEGIN;

ALTER TABLE public.leave_documents
    ADD COLUMN IF NOT EXISTS has_medical_certificate BOOLEAN;

COMMENT ON COLUMN public.leave_documents.has_medical_certificate IS
    'Required for sick leave: TRUE when a medical certificate is supplied, FALSE otherwise.';

COMMIT;
