BEGIN;

ALTER TABLE public.lotus_notes_employee_outbox
    ALTER COLUMN pre_employee_id DROP NOT NULL;

ALTER TABLE public.lotus_notes_employee_outbox
    ADD COLUMN IF NOT EXISTS employee_edit_request_id BIGINT
        REFERENCES public.employee_edit_requests(id);

CREATE UNIQUE INDEX IF NOT EXISTS ux_lotus_notes_employee_outbox_edit_request
    ON public.lotus_notes_employee_outbox(employee_edit_request_id)
    WHERE employee_edit_request_id IS NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_lotus_notes_employee_outbox_source'
          AND conrelid = 'public.lotus_notes_employee_outbox'::regclass
    ) THEN
        ALTER TABLE public.lotus_notes_employee_outbox
            ADD CONSTRAINT ck_lotus_notes_employee_outbox_source CHECK
            (
                (pre_employee_id IS NOT NULL AND employee_edit_request_id IS NULL)
                OR
                (pre_employee_id IS NULL AND employee_edit_request_id IS NOT NULL)
            );
    END IF;
END $$;

COMMIT;
