BEGIN;

CREATE TABLE IF NOT EXISTS public.employee_accounting_info
(
    employee_id       BIGINT PRIMARY KEY,
    bank_name         VARCHAR(200),
    bank_branch       VARCHAR(200),
    account_name      VARCHAR(250),
    account_number    VARCHAR(50),
    account_type      VARCHAR(100),
    promptpay_id      VARCHAR(50),
    created_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT fk_employee_accounting_info_employee
        FOREIGN KEY (employee_id) REFERENCES public.employees(id) ON DELETE CASCADE
);

DROP TRIGGER IF EXISTS trg_employee_accounting_info_updated_at
    ON public.employee_accounting_info;
CREATE TRIGGER trg_employee_accounting_info_updated_at
BEFORE UPDATE ON public.employee_accounting_info
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

INSERT INTO public.employee_accounting_info(employee_id)
SELECT id FROM public.employees
ON CONFLICT (employee_id) DO NOTHING;

COMMIT;
