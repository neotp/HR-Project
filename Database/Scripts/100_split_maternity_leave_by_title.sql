BEGIN;

-- Keep the old type ID so existing maternity documents retain their history.
UPDATE public.leave_types
SET name_th = 'ลาคลอดผู้หญิง', default_hours = 960, is_active = TRUE
WHERE code = 'UNPAID';

INSERT INTO public.leave_types (code, name_th, default_hours, is_active,
    default_bonus_deduction_enabled, default_bonus_deduction_percent)
SELECT 'PATERNITY', 'ลาคลอดผู้ชาย', 120, TRUE,
       default_bonus_deduction_enabled, default_bonus_deduction_percent
FROM public.leave_types WHERE code = 'UNPAID'
ON CONFLICT (code) DO UPDATE SET
    name_th = EXCLUDED.name_th,
    default_hours = EXCLUDED.default_hours,
    is_active = TRUE;

-- Adjust only auto-generated quotas from the current year onward. Preserve
-- manually adjusted quotas, historical years, used hours, and bonus entries.
UPDATE public.leave_quotas quota
SET quota_hours = 960,
    new_entitlement_hours = 960,
    updated_by = 'SYSTEM', updated_by_name = 'HR System'
FROM public.leave_types leave_type
WHERE quota.leave_type_id = leave_type.id
  AND leave_type.code = 'UNPAID'
  AND quota.quota_year >= EXTRACT(YEAR FROM CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok')
  AND quota.notes IN ('Created from leave type default hours',
                      'Initial quota from leave type master',
                      'Created automatically from leave_types.default_hours')
     OR (quota.leave_type_id = leave_type.id AND leave_type.code = 'UNPAID'
         AND quota.quota_year >= EXTRACT(YEAR FROM CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok')
         AND quota.notes LIKE 'Annual quota calculation for %');

-- Seed the new male quota for years already represented in the quota table.
-- Do not modify an existing male quota on a rerun.
WITH inserted AS
(
    INSERT INTO public.leave_quotas
        (employee_id, leave_type_id, quota_year, quota_hours, used_hours,
         notes, created_by, created_by_name, updated_by, updated_by_name,
         quota_status, new_entitlement_hours, carried_forward_hours,
         annual_excess_hours, finalized_at)
    SELECT DISTINCT employee.employee_code, male_type.id, existing.quota_year,
           120, 0, 'Initial male maternity quota',
           'SYSTEM', 'HR System', 'SYSTEM', 'HR System',
           'FINALIZED', 120, 0, 0, CURRENT_TIMESTAMP
    FROM public.leave_quotas existing
    JOIN public.employees employee ON employee.employee_code = existing.employee_id
    JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
    JOIN public.leave_types male_type ON male_type.code = 'PATERNITY'
    WHERE existing.quota_year >= EXTRACT(YEAR FROM CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok')
      AND employee.is_active = TRUE
      AND lower(regexp_replace(coalesce(basic.title, ''), '[.[:space:]]', '', 'g')) IN ('นาย', 'mr')
    ON CONFLICT (employee_id, leave_type_id, quota_year) DO NOTHING
    RETURNING id, quota_year
)
INSERT INTO public.leave_quota_history
    (leave_quota_id, action, details_text, before_data, after_data,
     action_by, action_by_name)
SELECT id, 'CREATE', 'Initial male maternity quota', NULL,
       jsonb_build_object('quotaHours', 120, 'quotaYear', quota_year, 'source', 'MATERNITY_SPLIT'),
       'SYSTEM', 'HR System'
FROM inserted;

COMMIT;
