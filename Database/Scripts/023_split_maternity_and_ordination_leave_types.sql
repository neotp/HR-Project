BEGIN;

-- Preserve the existing leave-type ID so documents and quotas that currently
-- reference UNPAID continue to work, but present it as maternity leave.
UPDATE public.leave_types
SET name_th = 'ลาคลอด',
    is_active = TRUE
WHERE code = 'UNPAID';

-- Ordination leave starts with exactly the same default hours as the former
-- combined maternity/ordination leave type. This statement is safe to rerun.
INSERT INTO public.leave_types (code, name_th, default_hours, is_active)
SELECT 'ORDINATION', 'ลาบวช', default_hours, TRUE
FROM public.leave_types
WHERE code = 'UNPAID'
ON CONFLICT (code) DO UPDATE SET
    name_th = EXCLUDED.name_th,
    default_hours = EXCLUDED.default_hours,
    is_active = TRUE;

-- Give every employee/year that already has the former combined quota a
-- separate ordination quota with the same quota hours. Used hours begin at 0
-- because historical leave documents remain attached to the original type.
WITH inserted_quotas AS
(
    INSERT INTO public.leave_quotas
        (employee_id, leave_type_id, quota_year, quota_hours, used_hours,
         notes, created_by, created_by_name, updated_by, updated_by_name)
    SELECT
        source_quota.employee_id,
        ordination.id,
        source_quota.quota_year,
        source_quota.quota_hours,
        0,
        'Created when maternity and ordination leave types were separated',
        'SYSTEM',
        'HR System',
        'SYSTEM',
        'HR System'
    FROM public.leave_quotas source_quota
    JOIN public.leave_types maternity
      ON maternity.id = source_quota.leave_type_id
     AND maternity.code = 'UNPAID'
    CROSS JOIN public.leave_types ordination
    WHERE ordination.code = 'ORDINATION'
    ON CONFLICT (employee_id, leave_type_id, quota_year) DO NOTHING
    RETURNING id, quota_hours
)
INSERT INTO public.leave_quota_history
    (leave_quota_id, action, details_text, before_data, after_data,
     action_by, action_by_name)
SELECT
    quota.id,
    'CREATE',
    'Created when maternity and ordination leave types were separated',
    NULL,
    jsonb_build_object(
        'quotaHours', quota.quota_hours,
        'source', 'UNPAID'),
    'SYSTEM',
    'HR System'
FROM inserted_quotas quota;

COMMIT;
