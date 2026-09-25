BEGIN;

ALTER TABLE public.leave_quotas
    ADD COLUMN IF NOT EXISTS quota_status VARCHAR(20) NOT NULL DEFAULT 'FINALIZED',
    ADD COLUMN IF NOT EXISTS new_entitlement_hours NUMERIC(10,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS carried_forward_hours NUMERIC(10,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS annual_excess_hours NUMERIC(10,2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS finalized_at TIMESTAMPTZ;

UPDATE public.leave_quotas
SET new_entitlement_hours = quota_hours,
    finalized_at = COALESCE(finalized_at, updated_at)
WHERE new_entitlement_hours = 0 AND quota_hours > 0;

ALTER TABLE public.leave_quotas DROP CONSTRAINT IF EXISTS ck_leave_quotas_status;
ALTER TABLE public.leave_quotas ADD CONSTRAINT ck_leave_quotas_status
    CHECK (quota_status IN ('PROJECTED', 'FINALIZED'));

CREATE TABLE IF NOT EXISTS public.leave_document_quota_allocations
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_document_id   BIGINT NOT NULL REFERENCES public.leave_documents(id) ON DELETE CASCADE,
    employee_id         VARCHAR(50) NOT NULL,
    leave_type_id       BIGINT NOT NULL REFERENCES public.leave_types(id),
    leave_year          SMALLINT NOT NULL,
    source_quota_year   SMALLINT NOT NULL,
    allocation_type     VARCHAR(30) NOT NULL,
    allocated_hours     NUMERIC(10,2) NOT NULL,
    allocated_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    released_at         TIMESTAMPTZ,
    release_reason      VARCHAR(80),
    CONSTRAINT ck_leave_document_quota_allocation_years
        CHECK (leave_year BETWEEN 2000 AND 2200 AND source_quota_year BETWEEN 2000 AND 2200),
    CONSTRAINT ck_leave_document_quota_allocation_hours CHECK (allocated_hours > 0),
    CONSTRAINT ck_leave_document_quota_allocation_type
        CHECK (allocation_type IN ('TARGET_YEAR', 'NEW_ENTITLEMENT', 'PRIOR_YEAR_BALANCE'))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_leave_document_quota_allocation_active
    ON public.leave_document_quota_allocations(leave_document_id, source_quota_year, allocation_type)
    WHERE released_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_leave_quota_allocation_source
    ON public.leave_document_quota_allocations(employee_id, leave_type_id, source_quota_year)
    WHERE released_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_leave_quota_allocation_leave_year
    ON public.leave_document_quota_allocations(employee_id, leave_type_id, leave_year)
    WHERE released_at IS NULL;

CREATE OR REPLACE FUNCTION public.is_working_day(target_date DATE)
RETURNS BOOLEAN
LANGUAGE sql
STABLE
AS $$
    SELECT
        NOT EXISTS
        (
            SELECT 1 FROM public.work_calendar_days d
            WHERE d.calendar_date = target_date AND d.day_type = 'PUBLIC_HOLIDAY'
        )
        AND
        (
            EXTRACT(ISODOW FROM target_date) BETWEEN 1 AND 5
            OR EXISTS
            (
                SELECT 1 FROM public.work_calendar_days d
                WHERE d.calendar_date = target_date AND d.day_type = 'WORKING_SATURDAY'
            )
        );
$$;

CREATE OR REPLACE FUNCTION public.first_working_day_of_month(target_date DATE)
RETURNS DATE
LANGUAGE sql
STABLE
AS $$
    SELECT day_value::DATE
    FROM generate_series(
        date_trunc('month', target_date)::DATE,
        (date_trunc('month', target_date) + INTERVAL '1 month - 1 day')::DATE,
        INTERVAL '1 day') day_value
    WHERE public.is_working_day(day_value::DATE)
    ORDER BY day_value
    LIMIT 1;
$$;

-- Vacation entitlement expressed in hours (8 hours = 1 day).
-- Hire year: 3 days, first following year: 9 days, second following
-- year: prorated from the hire month, then 12 days from the next year onward.
CREATE OR REPLACE FUNCTION public.calculate_vacation_entitlement_hours(
    target_employee_id VARCHAR,
    target_year INTEGER)
RETURNS NUMERIC(10,2)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    start_date_value DATE;
    start_year INTEGER;
    counted_months INTEGER;
BEGIN
    SELECT company.start_date
      INTO start_date_value
      FROM public.employees employee
      JOIN public.employee_company_info company ON company.employee_id = employee.id
     WHERE employee.employee_code = target_employee_id
     LIMIT 1;

    IF start_date_value IS NULL THEN RETURN 0; END IF;
    start_year := EXTRACT(YEAR FROM start_date_value)::INTEGER;
    IF target_year < start_year THEN RETURN 0;
    ELSIF target_year = start_year THEN RETURN 24;
    ELSIF target_year = start_year + 1 THEN RETURN 72;
    ELSIF target_year = start_year + 2 THEN
        counted_months := 13 - EXTRACT(MONTH FROM start_date_value)::INTEGER;
        IF start_date_value <> public.first_working_day_of_month(start_date_value) THEN
            counted_months := counted_months - 1;
        END IF;
        RETURN GREATEST(counted_months, 0) * 8;
    END IF;
    RETURN 96;
END;
$$;

CREATE OR REPLACE FUNCTION public.ensure_projected_vacation_quota(
    target_employee_id VARCHAR,
    target_leave_type_id BIGINT,
    target_year INTEGER)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    entitlement NUMERIC(10,2);
    previous_quota NUMERIC(10,2) := 0;
    previous_used NUMERIC(10,2) := 0;
    previous_remaining NUMERIC(10,2) := 0;
    opening_hours NUMERIC(10,2);
BEGIN
    entitlement := public.calculate_vacation_entitlement_hours(target_employee_id, target_year);

    SELECT COALESCE(q.quota_hours, 0)
      INTO previous_quota
      FROM public.leave_quotas q
     WHERE q.employee_id = target_employee_id
       AND q.leave_type_id = target_leave_type_id
       AND q.quota_year = target_year - 1;

    SELECT COALESCE(SUM(a.allocated_hours), 0)
      INTO previous_used
      FROM public.leave_document_quota_allocations a
     WHERE a.employee_id = target_employee_id
       AND a.leave_type_id = target_leave_type_id
       AND a.source_quota_year = target_year - 1
       AND a.released_at IS NULL;

    previous_remaining := GREATEST(previous_quota - previous_used, 0);
    opening_hours := LEAST(96, entitlement + previous_remaining);

    INSERT INTO public.leave_quotas
        (employee_id, leave_type_id, quota_year, quota_hours, used_hours, notes,
         created_by, created_by_name, updated_by, updated_by_name,
         quota_status, new_entitlement_hours, carried_forward_hours)
    VALUES
        (target_employee_id, target_leave_type_id, target_year, opening_hours, 0,
         'Projected vacation quota for advance leave booking',
         'SYSTEM', 'Annual leave quota system', 'SYSTEM', 'Annual leave quota system',
         'PROJECTED', entitlement, LEAST(previous_remaining, GREATEST(96 - entitlement, 0)))
    ON CONFLICT (employee_id, leave_type_id, quota_year) DO NOTHING;
END;
$$;

CREATE OR REPLACE FUNCTION public.ensure_initial_vacation_quota_for_employee()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    employee_code_value VARCHAR(50);
    vacation_type_id BIGINT;
    start_year_value SMALLINT;
BEGIN
    IF NEW.start_date IS NULL THEN RETURN NEW; END IF;
    SELECT employee_code INTO employee_code_value FROM public.employees WHERE id = NEW.employee_id;
    SELECT id INTO vacation_type_id FROM public.leave_types WHERE code = 'VACATION' AND is_active = TRUE;
    IF employee_code_value IS NULL OR vacation_type_id IS NULL THEN RETURN NEW; END IF;
    start_year_value := EXTRACT(YEAR FROM NEW.start_date)::SMALLINT;

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

DROP TRIGGER IF EXISTS trg_employee_initial_vacation_quota ON public.employee_company_info;
CREATE TRIGGER trg_employee_initial_vacation_quota
AFTER INSERT OR UPDATE OF start_date ON public.employee_company_info
FOR EACH ROW
WHEN (NEW.start_date IS NOT NULL)
EXECUTE FUNCTION public.ensure_initial_vacation_quota_for_employee();

CREATE OR REPLACE FUNCTION public.reserve_leave_document_quota(target_document_id BIGINT)
RETURNS VOID
LANGUAGE plpgsql
AS $$
DECLARE
    document_record RECORD;
    leave_type_code VARCHAR(30);
    leave_year_value SMALLINT;
    current_year_value SMALLINT := EXTRACT(YEAR FROM (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok'))::SMALLINT;
    target_new_available NUMERIC(10,2) := 0;
    current_available NUMERIC(10,2) := 0;
    target_amount NUMERIC(10,2) := 0;
    carry_amount NUMERIC(10,2) := 0;
    existing_future_usage NUMERIC(10,2) := 0;
    existing_carry_allocations NUMERIC(10,2) := 0;
    future_capacity NUMERIC(10,2) := 0;
    quota_id_value BIGINT;
BEGIN
    SELECT d.* INTO document_record FROM public.leave_documents d WHERE d.id = target_document_id;
    IF NOT FOUND OR document_record.status NOT IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED') THEN RETURN; END IF;

    leave_year_value := EXTRACT(YEAR FROM document_record.leave_date)::SMALLINT;
    SELECT code INTO leave_type_code FROM public.leave_types WHERE id = document_record.leave_type_id;

    IF leave_type_code = 'VACATION' AND leave_year_value = current_year_value + 1 THEN
        PERFORM public.ensure_projected_vacation_quota(
            document_record.creator_employee_id, document_record.leave_type_id, leave_year_value);

        PERFORM q.id
          FROM public.leave_quotas q
         WHERE q.employee_id = document_record.creator_employee_id
           AND q.leave_type_id = document_record.leave_type_id
           AND q.quota_year IN (current_year_value, leave_year_value)
         ORDER BY q.quota_year
         FOR UPDATE;

        SELECT GREATEST(q.new_entitlement_hours - COALESCE(SUM(a.allocated_hours), 0), 0)
          INTO target_new_available
          FROM public.leave_quotas q
          LEFT JOIN public.leave_document_quota_allocations a
            ON a.employee_id = q.employee_id AND a.leave_type_id = q.leave_type_id
           AND a.source_quota_year = q.quota_year AND a.allocation_type = 'NEW_ENTITLEMENT'
           AND a.released_at IS NULL
         WHERE q.employee_id = document_record.creator_employee_id
           AND q.leave_type_id = document_record.leave_type_id AND q.quota_year = leave_year_value
         GROUP BY q.new_entitlement_hours;

        SELECT GREATEST(q.quota_hours - COALESCE(SUM(a.allocated_hours), 0), 0)
          INTO current_available
          FROM public.leave_quotas q
          LEFT JOIN public.leave_document_quota_allocations a
            ON a.employee_id = q.employee_id AND a.leave_type_id = q.leave_type_id
           AND a.source_quota_year = q.quota_year AND a.released_at IS NULL
         WHERE q.employee_id = document_record.creator_employee_id
           AND q.leave_type_id = document_record.leave_type_id AND q.quota_year = current_year_value
         GROUP BY q.quota_hours;

        SELECT COALESCE(SUM(a.allocated_hours), 0),
               COALESCE(SUM(a.allocated_hours) FILTER
                   (WHERE a.source_quota_year = current_year_value), 0)
          INTO existing_future_usage, existing_carry_allocations
          FROM public.leave_document_quota_allocations a
         WHERE a.employee_id = document_record.creator_employee_id
           AND a.leave_type_id = document_record.leave_type_id
           AND a.leave_year = leave_year_value
           AND a.released_at IS NULL;

        future_capacity := LEAST(96,
            COALESCE(target_new_available, 0) + existing_future_usage
            + COALESCE(current_available, 0));
        IF existing_future_usage + document_record.leave_hours > future_capacity THEN
            RAISE EXCEPTION 'Vacation quota exceeds the 12-day future-year limit for document %',
                document_record.document_no USING ERRCODE = 'P0001';
        END IF;

        target_amount := LEAST(document_record.leave_hours, COALESCE(target_new_available, 0));
        carry_amount := document_record.leave_hours - target_amount;
        IF carry_amount > COALESCE(current_available, 0) THEN
            RAISE EXCEPTION 'Insufficient vacation quota for document %', document_record.document_no
                USING ERRCODE = 'P0001';
        END IF;

        IF target_amount > 0 THEN
            INSERT INTO public.leave_document_quota_allocations
                (leave_document_id, employee_id, leave_type_id, leave_year, source_quota_year,
                 allocation_type, allocated_hours)
            VALUES (document_record.id, document_record.creator_employee_id, document_record.leave_type_id,
                    leave_year_value, leave_year_value, 'NEW_ENTITLEMENT', target_amount);
        END IF;
        IF carry_amount > 0 THEN
            INSERT INTO public.leave_document_quota_allocations
                (leave_document_id, employee_id, leave_type_id, leave_year, source_quota_year,
                 allocation_type, allocated_hours)
            VALUES (document_record.id, document_record.creator_employee_id, document_record.leave_type_id,
                    leave_year_value, current_year_value, 'PRIOR_YEAR_BALANCE', carry_amount);
        END IF;
    ELSE
        IF leave_year_value > current_year_value + 1 THEN
            RAISE EXCEPTION 'Advance leave can be booked only through the next quota year' USING ERRCODE = 'P0001';
        END IF;
        SELECT q.id INTO quota_id_value
          FROM public.leave_quotas q
         WHERE q.employee_id = document_record.creator_employee_id
           AND q.leave_type_id = document_record.leave_type_id AND q.quota_year = leave_year_value
         FOR UPDATE;
        IF quota_id_value IS NULL THEN
            RAISE EXCEPTION 'Leave quota has not been configured for year %', leave_year_value USING ERRCODE = 'P0001';
        END IF;
        INSERT INTO public.leave_document_quota_allocations
            (leave_document_id, employee_id, leave_type_id, leave_year, source_quota_year,
             allocation_type, allocated_hours)
        VALUES (document_record.id, document_record.creator_employee_id, document_record.leave_type_id,
                leave_year_value, leave_year_value, 'TARGET_YEAR', document_record.leave_hours);
    END IF;

    INSERT INTO public.leave_quota_movements
        (leave_quota_id, employee_id, leave_type_id, quota_year, movement_type,
         source_type, source_id, reference_no, change_hours, notes, action_by, action_by_name)
    SELECT q.id, a.employee_id, a.leave_type_id, a.source_quota_year, 'LEAVE_RESERVED',
           'LEAVE_DOCUMENT', a.leave_document_id, document_record.document_no,
           -a.allocated_hours,
           CASE a.allocation_type WHEN 'NEW_ENTITLEMENT' THEN 'Reserve next-year new entitlement'
                WHEN 'PRIOR_YEAR_BALANCE' THEN 'Reserve prior-year remaining balance'
                ELSE 'Reserve leave quota' END,
           document_record.creator_employee_id, document_record.creator_name
    FROM public.leave_document_quota_allocations a
    LEFT JOIN public.leave_quotas q
      ON q.employee_id = a.employee_id AND q.leave_type_id = a.leave_type_id
     AND q.quota_year = a.source_quota_year
    WHERE a.leave_document_id = document_record.id AND a.released_at IS NULL;
END;
$$;

CREATE OR REPLACE FUNCTION public.manage_leave_document_quota_allocations()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    allocation_record RECORD;
    old_active BOOLEAN := FALSE;
    new_active BOOLEAN := FALSE;
    allocation_changed BOOLEAN := FALSE;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        old_active := OLD.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED');
    END IF;
    new_active := NEW.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED');

    IF TG_OP = 'UPDATE' AND OLD.document_no IS DISTINCT FROM NEW.document_no THEN
        UPDATE public.leave_quota_movements SET reference_no = NEW.document_no
        WHERE source_type = 'LEAVE_DOCUMENT' AND source_id = NEW.id;
    END IF;

    allocation_changed := TG_OP = 'INSERT'
        OR OLD.creator_employee_id IS DISTINCT FROM NEW.creator_employee_id
        OR OLD.leave_type_id IS DISTINCT FROM NEW.leave_type_id
        OR OLD.leave_date IS DISTINCT FROM NEW.leave_date
        OR OLD.leave_hours IS DISTINCT FROM NEW.leave_hours
        OR OLD.status IS DISTINCT FROM NEW.status;

    IF NOT allocation_changed THEN RETURN NEW; END IF;

    IF TG_OP <> 'INSERT' AND old_active THEN
        FOR allocation_record IN
            SELECT a.*, q.id AS quota_id
            FROM public.leave_document_quota_allocations a
            LEFT JOIN public.leave_quotas q
              ON q.employee_id = a.employee_id AND q.leave_type_id = a.leave_type_id
             AND q.quota_year = a.source_quota_year
            WHERE a.leave_document_id = OLD.id AND a.released_at IS NULL
            FOR UPDATE OF a
        LOOP
            INSERT INTO public.leave_quota_movements
                (leave_quota_id, employee_id, leave_type_id, quota_year, movement_type,
                 source_type, source_id, reference_no, change_hours, notes, action_by, action_by_name)
            VALUES (allocation_record.quota_id, allocation_record.employee_id,
                    allocation_record.leave_type_id, allocation_record.source_quota_year,
                    'LEAVE_RETURNED', 'LEAVE_DOCUMENT', OLD.id, NEW.document_no,
                    allocation_record.allocated_hours, 'Return quota to its original source year',
                    'SYSTEM', 'Annual leave quota system');
        END LOOP;
        UPDATE public.leave_document_quota_allocations
           SET released_at = CURRENT_TIMESTAMP,
               release_reason = CASE WHEN new_active THEN 'DOCUMENT_REALLOCATED' ELSE 'DOCUMENT_INACTIVE' END
         WHERE leave_document_id = OLD.id AND released_at IS NULL;
    END IF;

    IF new_active THEN PERFORM public.reserve_leave_document_quota(NEW.id); END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_leave_document_quota_movement ON public.leave_documents;
DROP TRIGGER IF EXISTS trg_leave_document_quota_allocation ON public.leave_documents;
CREATE TRIGGER trg_leave_document_quota_allocation
AFTER INSERT OR UPDATE OF document_no, creator_employee_id, leave_type_id, leave_date, leave_hours, status
ON public.leave_documents
FOR EACH ROW EXECUTE FUNCTION public.manage_leave_document_quota_allocations();

-- Existing active documents start with a source-year allocation. Future
-- documents are reallocated below so the new-entitlement-first rule applies.
INSERT INTO public.leave_document_quota_allocations
    (leave_document_id, employee_id, leave_type_id, leave_year, source_quota_year,
     allocation_type, allocated_hours, allocated_at)
SELECT d.id, d.creator_employee_id, d.leave_type_id,
       EXTRACT(YEAR FROM d.leave_date)::SMALLINT,
       EXTRACT(YEAR FROM d.leave_date)::SMALLINT,
       'TARGET_YEAR', d.leave_hours, d.created_at
FROM public.leave_documents d
WHERE d.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
  AND NOT EXISTS
      (SELECT 1 FROM public.leave_document_quota_allocations a
       WHERE a.leave_document_id = d.id AND a.released_at IS NULL);

-- Rebuild next-year vacation allocations according to the agreed priority.
DO $$
DECLARE doc RECORD;
BEGIN
    FOR doc IN
        SELECT d.id
        FROM public.leave_documents d
        JOIN public.leave_types t ON t.id = d.leave_type_id AND t.code = 'VACATION'
        WHERE d.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
          AND EXTRACT(YEAR FROM d.leave_date)::INT =
              EXTRACT(YEAR FROM (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok'))::INT + 1
          AND EXISTS
              (SELECT 1 FROM public.leave_document_quota_allocations a
               WHERE a.leave_document_id = d.id AND a.released_at IS NULL
                 AND a.allocation_type = 'TARGET_YEAR')
        ORDER BY d.created_at, d.id
    LOOP
        UPDATE public.leave_document_quota_allocations
           SET released_at = CURRENT_TIMESTAMP, release_reason = 'MIGRATION_REALLOCATION'
         WHERE leave_document_id = doc.id AND released_at IS NULL;
        PERFORM public.reserve_leave_document_quota(doc.id);
    END LOOP;
END $$;

-- Balance the legacy target-year reservation before the migration-created
-- source allocations. The NOT EXISTS guard keeps this repair idempotent.
INSERT INTO public.leave_quota_movements
    (leave_quota_id, employee_id, leave_type_id, quota_year, movement_type,
     source_type, source_id, reference_no, change_hours, notes, action_by, action_by_name)
SELECT q.id, a.employee_id, a.leave_type_id, a.source_quota_year,
       'MIGRATION_REALLOCATION_RETURN', 'LEAVE_DOCUMENT', a.leave_document_id,
       d.document_no, a.allocated_hours,
       'Balance legacy reservation before source-year allocation',
       'SYSTEM', 'Annual leave quota migration'
FROM public.leave_document_quota_allocations a
JOIN public.leave_documents d ON d.id = a.leave_document_id
LEFT JOIN public.leave_quotas q
  ON q.employee_id = a.employee_id AND q.leave_type_id = a.leave_type_id
 AND q.quota_year = a.source_quota_year
WHERE a.release_reason = 'MIGRATION_REALLOCATION'
  AND NOT EXISTS
      (SELECT 1 FROM public.leave_quota_movements movement
       WHERE movement.source_type = 'LEAVE_DOCUMENT'
         AND movement.source_id = a.leave_document_id
         AND movement.movement_type = 'MIGRATION_REALLOCATION_RETURN');

COMMIT;
