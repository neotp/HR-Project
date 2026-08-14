WITH inserted_quotas AS
(
    INSERT INTO public.leave_quotas
        (employee_id, leave_type_id, quota_year, quota_hours, used_hours,
         notes, created_by, created_by_name, updated_by, updated_by_name)
    SELECT
        employee.employee_code,
        leave_type.id,
        @quota_year,
        leave_type.default_hours,
        0,
        'Created from leave type default hours',
        'SYSTEM',
        'HR System',
        'SYSTEM',
        'HR System'
    FROM public.employees employee
    CROSS JOIN public.leave_types leave_type
    WHERE employee.is_active = TRUE
      AND leave_type.is_active = TRUE
    ON CONFLICT (employee_id, leave_type_id, quota_year) DO NOTHING
    RETURNING id, quota_hours
)
INSERT INTO public.leave_quota_history
    (leave_quota_id, action, details_text, before_data, after_data,
     action_by, action_by_name)
SELECT
    quota.id,
    'CREATE',
    'Created automatically from leave_types.default_hours',
    NULL,
    jsonb_build_object(
        'quotaHours', quota.quota_hours,
        'source', 'leave_types.default_hours'),
    'SYSTEM',
    'HR System'
FROM inserted_quotas quota;
