BEGIN;

CREATE OR REPLACE FUNCTION public.ensure_initial_vacation_quota_for_employee()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    employee_code_value VARCHAR(50);
    vacation_type_id BIGINT;
    start_year_value SMALLINT;
    current_year_value SMALLINT;
BEGIN
    IF NEW.start_date IS NULL THEN RETURN NEW; END IF;
    SELECT employee_code INTO employee_code_value FROM public.employees WHERE id = NEW.employee_id;
    SELECT id INTO vacation_type_id FROM public.leave_types WHERE code = 'VACATION' AND is_active = TRUE;
    IF employee_code_value IS NULL OR vacation_type_id IS NULL THEN RETURN NEW; END IF;

    current_year_value := EXTRACT(YEAR FROM CURRENT_DATE)::SMALLINT;
    start_year_value := GREATEST(
        EXTRACT(YEAR FROM NEW.start_date)::SMALLINT,
        current_year_value);

    INSERT INTO public.leave_quotas
        (employee_id, leave_type_id, quota_year, quota_hours, used_hours, notes,
         created_by, created_by_name, updated_by, updated_by_name,
         quota_status, new_entitlement_hours, carried_forward_hours, finalized_at)
    VALUES
        (employee_code_value, vacation_type_id, start_year_value, 24, 0,
         'Initial vacation entitlement: 3 days',
         'SYSTEM', 'Annual leave quota system', 'SYSTEM', 'Annual leave quota system',
         'FINALIZED', 24, 0, CURRENT_TIMESTAMP)
    ON CONFLICT (employee_id, leave_type_id, quota_year) DO NOTHING;
    RETURN NEW;
END;
$$;

COMMIT;
