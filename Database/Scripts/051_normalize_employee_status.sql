UPDATE public.employee_company_info
SET employee_status = CASE
    WHEN BTRIM(COALESCE(employee_status, '')) = 'ลาออก' THEN 'ลาออก'
    ELSE 'พนักงาน'
END
WHERE employee_status IS DISTINCT FROM CASE
    WHEN BTRIM(COALESCE(employee_status, '')) = 'ลาออก' THEN 'ลาออก'
    ELSE 'พนักงาน'
END;

ALTER TABLE public.employee_company_info
    DROP CONSTRAINT IF EXISTS ck_employee_company_info_employee_status;

ALTER TABLE public.employee_company_info
    ADD CONSTRAINT ck_employee_company_info_employee_status
    CHECK (employee_status IN ('พนักงาน', 'ลาออก'));
