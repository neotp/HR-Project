BEGIN;

ALTER TABLE public.leave_edit_requests
    ADD COLUMN IF NOT EXISTS requested_has_medical_certificate BOOLEAN;

COMMENT ON COLUMN public.leave_edit_requests.requested_has_medical_certificate IS
    'Requested medical-certificate state. Used only when the requested leave type is sick leave.';

COMMIT;
