BEGIN;

-- Initial rollout: every employee starts with the same Monday-Friday schedule.
-- Later changes are recorded as new effective-dated assignments by migration 103.
DO $$
DECLARE
    default_master_id BIGINT;
    default_version_id BIGINT;
    default_name CONSTANT TEXT := 'จันทร์-ศุกร์ 09:00-18:00';
BEGIN
    SELECT item.id INTO default_master_id
    FROM public.system_master_items item
    WHERE item.category_code = 'WORK_SCHEDULE'
      AND UPPER(item.item_code) = 'MON_FRI_0900_1800'
    ORDER BY item.id
    LIMIT 1;

    IF default_master_id IS NULL THEN
        INSERT INTO public.system_master_items
            (category_code, item_code, name_th, name_en, display_order, is_active)
        VALUES
            ('WORK_SCHEDULE', 'MON_FRI_0900_1800', default_name,
             'Monday-Friday 09:00-18:00', 0, TRUE)
        RETURNING id INTO default_master_id;
    ELSE
        UPDATE public.system_master_items
        SET name_th = default_name,
            name_en = 'Monday-Friday 09:00-18:00',
            display_order = 0,
            is_active = TRUE
        WHERE id = default_master_id;
    END IF;

    INSERT INTO public.work_schedule_versions(master_item_id, effective_from)
    VALUES (default_master_id, DATE '1900-01-01')
    ON CONFLICT (master_item_id, effective_from) DO UPDATE
        SET updated_at = CURRENT_TIMESTAMP
    RETURNING id INTO default_version_id;

    DELETE FROM public.work_schedule_days
    WHERE version_id = default_version_id;

    INSERT INTO public.work_schedule_days
        (version_id, iso_day_of_week, start_time, end_time,
         break_start_time, break_end_time)
    SELECT default_version_id, weekday,
           TIME '09:00', TIME '18:00', TIME '12:00', TIME '13:00'
    FROM generate_series(1, 5) weekday;

    -- Keep the existing employee field aligned with the selected Master so the
    -- employee detail and edit screens show the same value as Attendance.
    UPDATE public.employee_company_info
    SET work_schedule = default_name
    WHERE work_schedule IS DISTINCT FROM default_name;

    -- This is the initial schedule rollout, so replace only the newly introduced
    -- assignment ledger with one explicit baseline assignment per employee.
    DELETE FROM public.employee_work_schedule_assignments;

    INSERT INTO public.employee_work_schedule_assignments
        (employee_id, master_item_id, effective_from)
    SELECT employee.id, default_master_id, DATE '1900-01-01'
    FROM public.employees employee;

    -- Preserve old schedule Master rows for audit/aliases but hide them from new
    -- employee selections. They can be re-enabled and configured later if needed.
    UPDATE public.system_master_items
    SET is_active = (id = default_master_id)
    WHERE category_code = 'WORK_SCHEDULE';

    -- Pending Pre-Employees should receive the same initial schedule when they
    -- are later converted to employees.
    IF to_regclass('public.pre_employees') IS NOT NULL
       AND EXISTS
       (
           SELECT 1 FROM pg_attribute
           WHERE attrelid = to_regclass('public.pre_employees')
             AND attname = 'employee_data' AND NOT attisdropped
       ) THEN
        UPDATE public.pre_employees
        SET employee_data = jsonb_set(employee_data, '{workSchedule}',
                                      to_jsonb(default_name), TRUE),
            updated_at = CURRENT_TIMESTAMP
        WHERE employee_data IS NOT NULL
          AND employee_data ->> 'workSchedule' IS DISTINCT FROM default_name;
    END IF;
END;
$$;

COMMIT;
