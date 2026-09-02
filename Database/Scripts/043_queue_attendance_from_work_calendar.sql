CREATE OR REPLACE FUNCTION public.queue_attendance_recalculation_from_work_calendar()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP IN ('UPDATE', 'DELETE') THEN
        INSERT INTO public.attendance_recalculation_queue(employee_id, work_date, reason)
        SELECT employee.employee_code, OLD.calendar_date, 'WORK_CALENDAR_CHANGED'
        FROM public.employees employee
        LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
        WHERE employee.is_active = TRUE
          AND COALESCE(company.exclude_attendance_calculation, FALSE) = FALSE
        ON CONFLICT (employee_id, work_date) DO UPDATE
        SET reason = EXCLUDED.reason, requested_at = CURRENT_TIMESTAMP,
            attempts = 0, last_error = NULL;
    END IF;

    IF TG_OP IN ('INSERT', 'UPDATE') THEN
        INSERT INTO public.attendance_recalculation_queue(employee_id, work_date, reason)
        SELECT employee.employee_code, NEW.calendar_date, 'WORK_CALENDAR_CHANGED'
        FROM public.employees employee
        LEFT JOIN public.employee_company_info company ON company.employee_id = employee.id
        WHERE employee.is_active = TRUE
          AND COALESCE(company.exclude_attendance_calculation, FALSE) = FALSE
        ON CONFLICT (employee_id, work_date) DO UPDATE
        SET reason = EXCLUDED.reason, requested_at = CURRENT_TIMESTAMP,
            attempts = 0, last_error = NULL;
    END IF;

    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;

DROP TRIGGER IF EXISTS trg_work_calendar_attendance_recalculation
    ON public.work_calendar_days;
CREATE TRIGGER trg_work_calendar_attendance_recalculation
AFTER INSERT OR UPDATE OR DELETE ON public.work_calendar_days
FOR EACH ROW EXECUTE FUNCTION public.queue_attendance_recalculation_from_work_calendar();
