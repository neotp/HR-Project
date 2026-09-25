BEGIN;

ALTER TABLE public.employee_accounting_info
    ADD COLUMN IF NOT EXISTS social_security_number VARCHAR(50),
    ADD COLUMN IF NOT EXISTS taxpayer_identification_number VARCHAR(50),
    ADD COLUMN IF NOT EXISTS salary_account_number VARCHAR(50);

-- Keep an existing account number usable as the salary account until HR verifies it.
UPDATE public.employee_accounting_info
SET salary_account_number = account_number
WHERE NULLIF(BTRIM(salary_account_number), '') IS NULL
  AND NULLIF(BTRIM(account_number), '') IS NOT NULL;

COMMIT;
