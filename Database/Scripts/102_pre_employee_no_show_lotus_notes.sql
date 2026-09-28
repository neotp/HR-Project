BEGIN;

ALTER TABLE public.pre_employees
    ADD COLUMN IF NOT EXISTS did_not_start_work_reason TEXT;

-- A person who never started work has no row in public.employees. Keep the
-- existing outbox/worker, but allow its employee FK to be absent for this event.
ALTER TABLE public.lotus_notes_employee_outbox
    ALTER COLUMN employee_id DROP NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_lotus_notes_employee_outbox_no_show'
          AND conrelid = 'public.lotus_notes_employee_outbox'::regclass
    ) THEN
        ALTER TABLE public.lotus_notes_employee_outbox
            ADD CONSTRAINT ck_lotus_notes_employee_outbox_no_show CHECK
            (
                employee_id IS NOT NULL
                OR
                (
                    pre_employee_id IS NOT NULL
                    AND employee_edit_request_id IS NULL
                    AND payload ? 'CANCELSTARTREM'
                    AND payload ? 'CANCELSTARTBY'
                    AND payload ? 'CANCELSTARTAT'
                )
            );
    END IF;
END $$;

COMMIT;
