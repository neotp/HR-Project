ALTER TABLE public.employee_work_history
    ADD COLUMN IF NOT EXISTS business_unit VARCHAR(200),
    ADD COLUMN IF NOT EXISTS department_name VARCHAR(200),
    ADD COLUMN IF NOT EXISTS brand_name VARCHAR(200),
    ADD COLUMN IF NOT EXISTS comm_group_name VARCHAR(200);

COMMENT ON COLUMN public.employee_work_history.business_unit IS 'Business Unit during this work-history period';
COMMENT ON COLUMN public.employee_work_history.department_name IS 'Department during this work-history period';
COMMENT ON COLUMN public.employee_work_history.brand_name IS 'Brand during this work-history period';
COMMENT ON COLUMN public.employee_work_history.comm_group_name IS 'Comm.Group during this work-history period';
