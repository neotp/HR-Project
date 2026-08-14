BEGIN;

ALTER TABLE public.microsoft_accounts
    ALTER COLUMN employee_id DROP NOT NULL;

COMMENT ON COLUMN public.microsoft_accounts.employee_id IS
    'Nullable external employee key; populated when an employee is matched by email.';

COMMIT;
