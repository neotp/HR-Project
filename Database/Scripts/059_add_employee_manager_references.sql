ALTER TABLE public.employee_company_info
    ADD COLUMN IF NOT EXISTS supervisor_employee_id VARCHAR(50),
    ADD COLUMN IF NOT EXISTS leave_approver_employee_id VARCHAR(50);

CREATE INDEX IF NOT EXISTS ix_employee_company_info_supervisor_employee_id
    ON public.employee_company_info(supervisor_employee_id);

CREATE INDEX IF NOT EXISTS ix_employee_company_info_leave_approver_employee_id
    ON public.employee_company_info(leave_approver_employee_id);

UPDATE public.employee_company_info company
SET supervisor_employee_id =
(
    SELECT employee.employee_code
    FROM public.employees employee
    LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
    WHERE employee.is_active = TRUE
      AND
      (
          LOWER(BTRIM(COALESCE(company.supervisor_name, ''))) = LOWER(BTRIM(employee.employee_code))
          OR LOWER(BTRIM(COALESCE(company.supervisor_name, ''))) = LOWER(BTRIM(COALESCE(basic.full_name_th, '')))
          OR LOWER(BTRIM(COALESCE(company.supervisor_name, ''))) = LOWER(BTRIM(COALESCE(basic.full_name_en, '')))
      )
    ORDER BY employee.id
    LIMIT 1
)
WHERE NULLIF(BTRIM(COALESCE(company.supervisor_employee_id, '')), '') IS NULL
  AND NULLIF(BTRIM(COALESCE(company.supervisor_name, '')), '') IS NOT NULL;

UPDATE public.employee_company_info company
SET leave_approver_employee_id =
(
    SELECT employee.employee_code
    FROM public.employees employee
    LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
    WHERE employee.is_active = TRUE
      AND
      (
          LOWER(BTRIM(COALESCE(company.leave_approver_name, ''))) = LOWER(BTRIM(employee.employee_code))
          OR LOWER(BTRIM(COALESCE(company.leave_approver_name, ''))) = LOWER(BTRIM(COALESCE(basic.full_name_th, '')))
          OR LOWER(BTRIM(COALESCE(company.leave_approver_name, ''))) = LOWER(BTRIM(COALESCE(basic.full_name_en, '')))
      )
    ORDER BY employee.id
    LIMIT 1
)
WHERE NULLIF(BTRIM(COALESCE(company.leave_approver_employee_id, '')), '') IS NULL
  AND NULLIF(BTRIM(COALESCE(company.leave_approver_name, '')), '') IS NOT NULL;
