BEGIN;

ALTER TABLE public.leave_types
    ADD COLUMN IF NOT EXISTS default_bonus_deduction_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS default_bonus_deduction_percent NUMERIC(5,2) NOT NULL DEFAULT 0;

ALTER TABLE public.leave_types
    DROP CONSTRAINT IF EXISTS ck_leave_types_bonus_deduction_percent;
ALTER TABLE public.leave_types
    ADD CONSTRAINT ck_leave_types_bonus_deduction_percent
        CHECK (default_bonus_deduction_percent >= 0 AND default_bonus_deduction_percent <= 100);

UPDATE public.leave_types
SET default_bonus_deduction_enabled = CASE code
        WHEN 'SICK' THEN TRUE
        WHEN 'PERSONAL' THEN TRUE
        WHEN 'UNPAID' THEN TRUE
        WHEN 'ORDINATION' THEN TRUE
        ELSE FALSE
    END,
    default_bonus_deduction_percent = CASE code
        WHEN 'SICK' THEN 10
        WHEN 'PERSONAL' THEN 5
        WHEN 'UNPAID' THEN 5
        WHEN 'ORDINATION' THEN 5
        ELSE 0
    END;

ALTER TABLE public.leave_documents
    ADD COLUMN IF NOT EXISTS bonus_deduction_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS bonus_deduction_percent NUMERIC(5,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS bonus_deduction_waived BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS bonus_deduction_reason TEXT,
    ADD COLUMN IF NOT EXISTS bonus_deduction_overridden BOOLEAN NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS bonus_deduction_updated_by VARCHAR(50),
    ADD COLUMN IF NOT EXISTS bonus_deduction_updated_by_name VARCHAR(200),
    ADD COLUMN IF NOT EXISTS bonus_deduction_updated_at TIMESTAMPTZ;

ALTER TABLE public.leave_documents
    DROP CONSTRAINT IF EXISTS ck_leave_documents_bonus_deduction_percent;
ALTER TABLE public.leave_documents
    ADD CONSTRAINT ck_leave_documents_bonus_deduction_percent
        CHECK (bonus_deduction_percent >= 0 AND bonus_deduction_percent <= 100);

ALTER TABLE public.leave_document_history
    DROP CONSTRAINT IF EXISTS ck_leave_history_action;
ALTER TABLE public.leave_document_history
    ADD CONSTRAINT ck_leave_history_action
        CHECK (action IN
        (
            'CREATE_DOCUMENT', 'APPROVE', 'REJECT', 'EDIT', 'CANCEL',
            'REQUEST_EDIT', 'CANCEL_EDIT_REQUEST', 'APPROVE_EDIT_REQUEST',
            'REQUEST_CANCEL', 'CANCEL_CANCEL_REQUEST',
            'APPROVE_CANCEL_REQUEST', 'REJECT_CANCEL_REQUEST',
            'BONUS_DEDUCTION_UPDATE'
        ));

-- Existing documents receive a snapshot of the current policy. Sick leave uses
-- the medical-certificate rule from the supplied policy image.
UPDATE public.leave_documents document
SET bonus_deduction_enabled = leave_type.default_bonus_deduction_enabled,
    bonus_deduction_percent = CASE
        WHEN leave_type.code = 'SICK' AND document.has_medical_certificate IS TRUE THEN 5
        WHEN leave_type.code = 'SICK' THEN 10
        ELSE leave_type.default_bonus_deduction_percent
    END
FROM public.leave_types leave_type
WHERE leave_type.id = document.leave_type_id
  AND document.bonus_deduction_overridden = FALSE;

WITH action_seed(page_key, action_key, action_name, description, display_order) AS
(
    VALUES
      ('LEAVE_ALL_DOCUMENTS', 'VIEW_BONUS_DEDUCTION', 'ดูข้อมูลการหักโบนัส',
       'ดูสถานะและเปอร์เซ็นต์การหักโบนัสของเอกสารการลา', 20),
      ('LEAVE_ALL_DOCUMENTS', 'EDIT_BONUS_DEDUCTION', 'แก้ไขข้อมูลการหักโบนัส',
       'ปรับเปอร์เซ็นต์หรือยกเว้นการหักโบนัสเป็นรายเอกสาร', 30)
)
INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT page.id, seed.action_key, seed.action_name, seed.description, seed.display_order
FROM action_seed seed
JOIN public.application_pages page ON page.page_key = seed.page_key
ON CONFLICT (application_page_id, action_key) DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

COMMIT;
