BEGIN;

-- An imported six-digit code can represent an existing four-digit employee.
-- Keep the employee's real-code quota and remove only the duplicate quota
-- whose employee_id has no employee row or business activity attached to it.
CREATE TEMP TABLE orphan_leave_quota_codes ON COMMIT DROP AS
SELECT DISTINCT quota.employee_id
FROM public.leave_quotas quota
LEFT JOIN public.employees employee ON employee.employee_code = quota.employee_id
WHERE employee.id IS NULL
  AND quota.employee_id ~ '^[0-9]{6}$'
  AND EXISTS
  (
      SELECT 1 FROM public.employees real_employee
      WHERE real_employee.employee_code ~ '^[0-9]{1,5}$'
        AND lpad(real_employee.employee_code, 6, '0') = quota.employee_id
  )
  AND NOT EXISTS (SELECT 1 FROM public.leave_documents document
                  WHERE document.creator_employee_id = quota.employee_id)
  AND NOT EXISTS (SELECT 1 FROM public.leave_document_quota_allocations allocation
                  WHERE allocation.employee_id = quota.employee_id)
  AND NOT EXISTS (SELECT 1 FROM public.leave_quota_requests request
                  WHERE request.employee_id = quota.employee_id)
  AND NOT EXISTS (SELECT 1 FROM public.leave_quota_yearly_rollovers rollover
                  WHERE rollover.employee_id = quota.employee_id)
  AND NOT EXISTS (SELECT 1 FROM public.leave_quota_excess_details excess
                  WHERE excess.employee_id = quota.employee_id)
  AND NOT EXISTS
  (
      SELECT 1 FROM public.leave_quotas other_quota
      WHERE other_quota.employee_id = quota.employee_id
        AND (other_quota.quota_year <> 2026 OR other_quota.used_hours <> 0)
  )
  AND NOT EXISTS
  (
      SELECT 1 FROM public.leave_quota_movements movement
      WHERE movement.employee_id = quota.employee_id
        AND movement.movement_type NOT IN ('OPENING_QUOTA', 'QUOTA_CREATED')
  )
  AND NOT EXISTS
  (
      SELECT 1 FROM public.leave_quota_history history
      JOIN public.leave_quotas other_quota ON other_quota.id = history.leave_quota_id
      WHERE other_quota.employee_id = quota.employee_id AND history.action <> 'CREATE'
  );

DO $$
BEGIN
    IF (SELECT count(*) FROM orphan_leave_quota_codes) NOT IN (0, 970) OR
       ((SELECT count(*) FROM orphan_leave_quota_codes) = 970 AND
        (SELECT count(*) FROM public.leave_quotas quota
         JOIN orphan_leave_quota_codes codes USING (employee_id)) <> 4850) THEN
        RAISE EXCEPTION 'Orphan quota scope changed; stop and audit again';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS public.leave_quota_orphan_archive_20260925
(
    source_table TEXT NOT NULL,
    source_id BIGINT NOT NULL,
    row_data JSONB NOT NULL,
    archived_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (source_table, source_id)
);

INSERT INTO public.leave_quota_orphan_archive_20260925(source_table, source_id, row_data)
SELECT 'leave_quotas', quota.id, to_jsonb(quota)
FROM public.leave_quotas quota
JOIN orphan_leave_quota_codes codes USING (employee_id)
ON CONFLICT DO NOTHING;

INSERT INTO public.leave_quota_orphan_archive_20260925(source_table, source_id, row_data)
SELECT 'leave_quota_history', history.id, to_jsonb(history)
FROM public.leave_quota_history history
JOIN public.leave_quotas quota ON quota.id = history.leave_quota_id
JOIN orphan_leave_quota_codes codes ON codes.employee_id = quota.employee_id
ON CONFLICT DO NOTHING;

INSERT INTO public.leave_quota_orphan_archive_20260925(source_table, source_id, row_data)
SELECT 'leave_quota_movements', movement.id, to_jsonb(movement)
FROM public.leave_quota_movements movement
JOIN orphan_leave_quota_codes codes USING (employee_id)
ON CONFLICT DO NOTHING;

-- Deleting quotas writes QUOTA_REMOVED movements via a trigger. Delete the
-- whole orphan ledger after deleting quotas, leaving real employees untouched.
DELETE FROM public.leave_quotas quota
USING orphan_leave_quota_codes codes
WHERE quota.employee_id = codes.employee_id;

DELETE FROM public.leave_quota_movements movement
USING orphan_leave_quota_codes codes
WHERE movement.employee_id = codes.employee_id;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM public.leave_quotas quota
               JOIN orphan_leave_quota_codes codes USING (employee_id)) OR
       EXISTS (SELECT 1 FROM public.leave_quota_movements movement
               JOIN orphan_leave_quota_codes codes USING (employee_id)) THEN
        RAISE EXCEPTION 'Orphan quota cleanup verification failed';
    END IF;
END $$;

COMMIT;
