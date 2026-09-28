BEGIN;

-- A version owns a complete weekly pattern. Missing weekdays are days off.
-- Existing master values and assignments receive the former weekday pattern.
CREATE TABLE IF NOT EXISTS public.work_schedule_versions
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    master_item_id  BIGINT NOT NULL REFERENCES public.system_master_items(id) ON DELETE RESTRICT,
    effective_from  DATE NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_work_schedule_version_effective UNIQUE (master_item_id, effective_from)
);

CREATE INDEX IF NOT EXISTS ix_work_schedule_versions_lookup
    ON public.work_schedule_versions(master_item_id, effective_from DESC);

DROP TRIGGER IF EXISTS trg_work_schedule_versions_updated_at ON public.work_schedule_versions;
CREATE TRIGGER trg_work_schedule_versions_updated_at
BEFORE UPDATE ON public.work_schedule_versions
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

CREATE TABLE IF NOT EXISTS public.work_schedule_days
(
    version_id      BIGINT NOT NULL REFERENCES public.work_schedule_versions(id) ON DELETE CASCADE,
    iso_day_of_week SMALLINT NOT NULL CHECK (iso_day_of_week BETWEEN 1 AND 7),
    start_time      TIME NOT NULL,
    end_time        TIME NOT NULL,
    break_start_time TIME NOT NULL,
    break_end_time   TIME NOT NULL,
    PRIMARY KEY (version_id, iso_day_of_week),
    CONSTRAINT ck_work_schedule_day_time_order CHECK
    (
        start_time < break_start_time
        AND break_start_time < break_end_time
        AND break_end_time < end_time
    )
);

CREATE TABLE IF NOT EXISTS public.employee_work_schedule_assignments
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id     BIGINT NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    master_item_id  BIGINT REFERENCES public.system_master_items(id) ON DELETE RESTRICT,
    effective_from  DATE NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_employee_work_schedule_assignment_effective UNIQUE (employee_id, effective_from)
);

CREATE INDEX IF NOT EXISTS ix_employee_work_schedule_assignments_lookup
    ON public.employee_work_schedule_assignments(employee_id, effective_from DESC);
CREATE INDEX IF NOT EXISTS ix_employee_work_schedule_assignments_master
    ON public.employee_work_schedule_assignments(master_item_id, effective_from);

CREATE TABLE IF NOT EXISTS public.work_schedule_aliases
(
    master_item_id BIGINT NOT NULL REFERENCES public.system_master_items(id) ON DELETE CASCADE,
    alias_value    VARCHAR(250) NOT NULL,
    PRIMARY KEY (master_item_id, alias_value)
);

CREATE INDEX IF NOT EXISTS ix_work_schedule_aliases_value
    ON public.work_schedule_aliases(LOWER(alias_value));

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT DISTINCT 'WORK_SCHEDULE',
       CASE WHEN LENGTH(BTRIM(company.work_schedule)) <= 100
            THEN BTRIM(company.work_schedule)
            ELSE LEFT(BTRIM(company.work_schedule), 67) || '_' || MD5(BTRIM(company.work_schedule)) END,
       LEFT(BTRIM(company.work_schedule), 250), LEFT(BTRIM(company.work_schedule), 250)
FROM public.employee_company_info company
WHERE NULLIF(BTRIM(company.work_schedule), '') IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1 FROM public.system_master_items item
      WHERE item.category_code = 'WORK_SCHEDULE'
        AND (LOWER(item.item_code) = LOWER(BTRIM(company.work_schedule))
             OR LOWER(item.name_th) = LOWER(BTRIM(company.work_schedule))
             OR EXISTS
             (
                 SELECT 1 FROM public.work_schedule_aliases alias
                 WHERE alias.master_item_id = item.id
                   AND LOWER(alias.alias_value) = LOWER(BTRIM(company.work_schedule))
             ))
  )
ON CONFLICT DO NOTHING;

-- Pending Pre-Employees may have schedules that have not reached the employee
-- table yet. Register those legacy values before future conversions are strict.
DO $$
BEGIN
    IF to_regclass('public.pre_employees') IS NOT NULL
       AND EXISTS
       (
           SELECT 1 FROM pg_attribute
           WHERE attrelid = to_regclass('public.pre_employees')
             AND attname = 'employee_data' AND NOT attisdropped
       ) THEN
        INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
        SELECT DISTINCT 'WORK_SCHEDULE',
               CASE WHEN LENGTH(BTRIM(pre.employee_data ->> 'workSchedule')) <= 100
                    THEN BTRIM(pre.employee_data ->> 'workSchedule')
                    ELSE LEFT(BTRIM(pre.employee_data ->> 'workSchedule'), 67) || '_' ||
                         MD5(BTRIM(pre.employee_data ->> 'workSchedule')) END,
               LEFT(BTRIM(pre.employee_data ->> 'workSchedule'), 250),
               LEFT(BTRIM(pre.employee_data ->> 'workSchedule'), 250)
        FROM public.pre_employees pre
        WHERE NULLIF(BTRIM(pre.employee_data ->> 'workSchedule'), '') IS NOT NULL
          AND NOT EXISTS
          (
              SELECT 1 FROM public.system_master_items item
              WHERE item.category_code = 'WORK_SCHEDULE'
                AND (LOWER(item.item_code) = LOWER(BTRIM(pre.employee_data ->> 'workSchedule'))
                     OR LOWER(item.name_th) = LOWER(BTRIM(pre.employee_data ->> 'workSchedule'))
                     OR EXISTS
                     (
                         SELECT 1 FROM public.work_schedule_aliases alias
                         WHERE alias.master_item_id = item.id
                           AND LOWER(alias.alias_value) = LOWER(BTRIM(pre.employee_data ->> 'workSchedule'))
                     ))
          )
        ON CONFLICT DO NOTHING;
    END IF;
END;
$$;

INSERT INTO public.work_schedule_versions(master_item_id, effective_from)
SELECT item.id, DATE '1900-01-01'
FROM public.system_master_items item
WHERE item.category_code = 'WORK_SCHEDULE'
  AND NOT EXISTS
  (
      SELECT 1 FROM public.work_schedule_versions version
      WHERE version.master_item_id = item.id
  )
ON CONFLICT (master_item_id, effective_from) DO NOTHING;

INSERT INTO public.work_schedule_days
    (version_id, iso_day_of_week, start_time, end_time, break_start_time, break_end_time)
SELECT version.id, weekday.iso_day_of_week,
       TIME '09:00', TIME '18:00', TIME '12:00', TIME '13:00'
FROM public.work_schedule_versions version
JOIN public.system_master_items item ON item.id = version.master_item_id
CROSS JOIN generate_series(1, 5) weekday(iso_day_of_week)
WHERE item.category_code = 'WORK_SCHEDULE'
  AND version.effective_from = DATE '1900-01-01'
ON CONFLICT (version_id, iso_day_of_week) DO NOTHING;

INSERT INTO public.employee_work_schedule_assignments
    (employee_id, master_item_id, effective_from)
SELECT company.employee_id, item.id, DATE '1900-01-01'
FROM public.employee_company_info company
JOIN LATERAL
(
    SELECT master.id
    FROM public.system_master_items master
    LEFT JOIN public.work_schedule_aliases alias
      ON alias.master_item_id = master.id
     AND LOWER(alias.alias_value) = LOWER(BTRIM(company.work_schedule))
    WHERE master.category_code = 'WORK_SCHEDULE'
      AND (LOWER(master.item_code) = LOWER(BTRIM(company.work_schedule))
           OR LOWER(master.name_th) = LOWER(BTRIM(company.work_schedule))
           OR alias.master_item_id IS NOT NULL)
    ORDER BY CASE WHEN LOWER(master.item_code) = LOWER(BTRIM(company.work_schedule)) THEN 0
                  WHEN LOWER(master.name_th) = LOWER(BTRIM(company.work_schedule)) THEN 1
                  ELSE 2 END,
             master.id
    LIMIT 1
) item ON TRUE
WHERE NULLIF(BTRIM(company.work_schedule), '') IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1 FROM public.employee_work_schedule_assignments existing
      WHERE existing.employee_id = company.employee_id
  )
ON CONFLICT (employee_id, effective_from) DO NOTHING;

-- Keep former codes/names resolvable after a master is renamed. Existing
-- employee text and historical assignment rows are left untouched.
CREATE OR REPLACE FUNCTION public.remember_work_schedule_aliases()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    IF OLD.category_code = 'WORK_SCHEDULE' AND NEW.category_code = 'WORK_SCHEDULE' THEN
        IF OLD.item_code IS DISTINCT FROM NEW.item_code THEN
            INSERT INTO public.work_schedule_aliases(master_item_id, alias_value)
            VALUES (NEW.id, OLD.item_code)
            ON CONFLICT (master_item_id, alias_value) DO NOTHING;
        END IF;
        IF OLD.name_th IS DISTINCT FROM NEW.name_th THEN
            INSERT INTO public.work_schedule_aliases(master_item_id, alias_value)
            VALUES (NEW.id, OLD.name_th)
            ON CONFLICT (master_item_id, alias_value) DO NOTHING;
        END IF;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_work_schedule_remember_aliases ON public.system_master_items;
CREATE TRIGGER trg_work_schedule_remember_aliases
AFTER UPDATE OF item_code, name_th ON public.system_master_items
FOR EACH ROW EXECUTE FUNCTION public.remember_work_schedule_aliases();

-- Changes to the existing employee field create a new dated assignment.
-- A NULL assignment records a deliberate return to the legacy default.
CREATE OR REPLACE FUNCTION public.sync_employee_work_schedule_assignment()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    schedule_value TEXT;
    schedule_master_id BIGINT;
    assignment_from DATE;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF OLD.work_schedule IS NOT DISTINCT FROM NEW.work_schedule THEN
            RETURN NEW;
        END IF;
    END IF;

    schedule_value := NULLIF(BTRIM(NEW.work_schedule), '');
    IF schedule_value IS NOT NULL THEN
        SELECT item.id INTO schedule_master_id
        FROM public.system_master_items item
        LEFT JOIN public.work_schedule_aliases alias
          ON alias.master_item_id = item.id
         AND LOWER(alias.alias_value) = LOWER(schedule_value)
        WHERE item.category_code = 'WORK_SCHEDULE'
          AND (LOWER(item.item_code) = LOWER(schedule_value)
               OR LOWER(item.name_th) = LOWER(schedule_value)
               OR alias.master_item_id IS NOT NULL)
        ORDER BY CASE WHEN LOWER(item.item_code) = LOWER(schedule_value) THEN 0
                      WHEN LOWER(item.name_th) = LOWER(schedule_value) THEN 1
                      ELSE 2 END,
                 item.id
        LIMIT 1;

        IF schedule_master_id IS NULL THEN
            RAISE EXCEPTION 'Unknown work schedule "%" for employee %',
                schedule_value, NEW.employee_id USING ERRCODE = '22023';
        END IF;
    END IF;

    assignment_from := CASE WHEN TG_OP = 'INSERT'
        THEN COALESCE(NEW.start_date, (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok')::DATE)
        ELSE (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Bangkok')::DATE END;

    INSERT INTO public.employee_work_schedule_assignments
        (employee_id, master_item_id, effective_from)
    VALUES (NEW.employee_id, schedule_master_id, assignment_from)
    ON CONFLICT (employee_id, effective_from) DO UPDATE
        SET master_item_id = EXCLUDED.master_item_id;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_employee_work_schedule_assignment ON public.employee_company_info;
CREATE TRIGGER trg_employee_work_schedule_assignment
AFTER INSERT OR UPDATE OF work_schedule ON public.employee_company_info
FOR EACH ROW EXECUTE FUNCTION public.sync_employee_work_schedule_assignment();

-- One row for every input date. Holidays take precedence; a designated working
-- Saturday uses the employee's Saturday hours when present, otherwise 09–17.
CREATE OR REPLACE FUNCTION public.get_employee_work_schedule(
    p_employee_code TEXT,
    p_work_date DATE)
RETURNS TABLE
(
    is_work_day BOOLEAN,
    work_start TIME,
    work_end TIME,
    break_start TIME,
    break_end TIME
)
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    selected_master_id BIGINT;
    selected_version_id BIGINT;
    selected_day public.work_schedule_days%ROWTYPE;
    is_special_saturday BOOLEAN;
    iso_weekday INTEGER;
BEGIN
    is_work_day := FALSE;
    work_start := NULL;
    work_end := NULL;
    break_start := NULL;
    break_end := NULL;

    IF p_work_date IS NULL THEN
        RETURN NEXT;
        RETURN;
    END IF;

    IF EXISTS
    (
        SELECT 1 FROM public.work_calendar_days calendar
        WHERE calendar.calendar_date = p_work_date
          AND calendar.day_type = 'PUBLIC_HOLIDAY'
    ) THEN
        RETURN NEXT;
        RETURN;
    END IF;

    iso_weekday := EXTRACT(ISODOW FROM p_work_date)::INTEGER;
    is_special_saturday := EXISTS
    (
        SELECT 1 FROM public.work_calendar_days calendar
        WHERE calendar.calendar_date = p_work_date
          AND calendar.day_type = 'WORKING_SATURDAY'
    );

    SELECT assignment.master_item_id INTO selected_master_id
    FROM public.employees employee
    JOIN public.employee_work_schedule_assignments assignment
      ON assignment.employee_id = employee.id
    WHERE employee.employee_code = p_employee_code
      AND assignment.effective_from <= p_work_date
    ORDER BY assignment.effective_from DESC
    LIMIT 1;

    IF selected_master_id IS NOT NULL THEN
        SELECT version.id INTO selected_version_id
        FROM public.work_schedule_versions version
        WHERE version.master_item_id = selected_master_id
          AND version.effective_from <= p_work_date
        ORDER BY version.effective_from DESC
        LIMIT 1;

        IF selected_version_id IS NOT NULL THEN
            SELECT day.* INTO selected_day
            FROM public.work_schedule_days day
            WHERE day.version_id = selected_version_id
              AND day.iso_day_of_week = iso_weekday;
            IF FOUND THEN
                is_work_day := TRUE;
                work_start := selected_day.start_time;
                work_end := selected_day.end_time;
                break_start := selected_day.break_start_time;
                break_end := selected_day.break_end_time;
                RETURN NEXT;
                RETURN;
            END IF;
        END IF;
    END IF;

    IF is_special_saturday THEN
        is_work_day := TRUE;
        work_start := TIME '09:00';
        work_end := TIME '17:00';
        break_start := TIME '12:00';
        break_end := TIME '13:00';
    ELSIF selected_version_id IS NULL AND iso_weekday BETWEEN 1 AND 5 THEN
        is_work_day := TRUE;
        work_start := TIME '09:00';
        work_end := TIME '18:00';
        break_start := TIME '12:00';
        break_end := TIME '13:00';
    END IF;

    RETURN NEXT;
END;
$$;

-- Recalculate only dates already represented in attendance. Future scans are
-- calculated from the new schedule when they arrive.
CREATE OR REPLACE FUNCTION public.queue_attendance_from_schedule_assignment()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    changed_employee_id BIGINT;
    changed_from DATE;
BEGIN
    changed_employee_id := CASE WHEN TG_OP = 'DELETE' THEN OLD.employee_id ELSE NEW.employee_id END;
    changed_from := CASE WHEN TG_OP = 'DELETE' THEN OLD.effective_from ELSE NEW.effective_from END;

    INSERT INTO public.attendance_recalculation_queue(employee_id, work_date, reason)
    SELECT employee.employee_code, daily.work_date, 'WORK_SCHEDULE_CHANGED'
    FROM public.employees employee
    JOIN public.attendance_daily_records daily ON daily.employee_id = employee.employee_code
    WHERE employee.id = changed_employee_id AND daily.work_date >= changed_from
    ON CONFLICT (employee_id, work_date) DO UPDATE SET
        reason = EXCLUDED.reason, requested_at = CURRENT_TIMESTAMP,
        attempts = 0, last_error = NULL;

    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;

DROP TRIGGER IF EXISTS trg_work_schedule_assignment_recalculation
    ON public.employee_work_schedule_assignments;
CREATE TRIGGER trg_work_schedule_assignment_recalculation
AFTER INSERT OR UPDATE OR DELETE ON public.employee_work_schedule_assignments
FOR EACH ROW EXECUTE FUNCTION public.queue_attendance_from_schedule_assignment();

CREATE OR REPLACE FUNCTION public.queue_attendance_from_schedule_version()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    affected_version_id BIGINT;
    affected_master_id BIGINT;
    affected_from DATE;
    affected_iso_day SMALLINT;
BEGIN
    IF TG_TABLE_NAME = 'work_schedule_versions' THEN
        IF TG_OP = 'DELETE' THEN
            affected_master_id := OLD.master_item_id;
            affected_from := OLD.effective_from;
        ELSE
            affected_master_id := NEW.master_item_id;
            affected_from := NEW.effective_from;
        END IF;
    ELSE
        IF TG_OP = 'DELETE' THEN
            affected_version_id := OLD.version_id;
            affected_iso_day := OLD.iso_day_of_week;
        ELSE
            affected_version_id := NEW.version_id;
            affected_iso_day := NEW.iso_day_of_week;
            IF TG_OP = 'UPDATE' AND OLD.iso_day_of_week IS DISTINCT FROM NEW.iso_day_of_week THEN
                affected_iso_day := NULL;
            END IF;
        END IF;
        SELECT version.master_item_id, version.effective_from
          INTO affected_master_id, affected_from
        FROM public.work_schedule_versions version
        WHERE version.id = affected_version_id;
    END IF;

    IF affected_master_id IS NOT NULL THEN
        INSERT INTO public.attendance_recalculation_queue(employee_id, work_date, reason)
        SELECT daily.employee_id, daily.work_date, 'WORK_SCHEDULE_CHANGED'
        FROM public.attendance_daily_records daily
        JOIN public.employees employee ON employee.employee_code = daily.employee_id
        WHERE daily.work_date >= affected_from
          AND (affected_iso_day IS NULL
               OR EXTRACT(ISODOW FROM daily.work_date) = affected_iso_day)
          AND affected_master_id =
          (
              SELECT assignment.master_item_id
              FROM public.employee_work_schedule_assignments assignment
              WHERE assignment.employee_id = employee.id
                AND assignment.effective_from <= daily.work_date
              ORDER BY assignment.effective_from DESC
              LIMIT 1
          )
        ON CONFLICT (employee_id, work_date) DO UPDATE SET
            reason = EXCLUDED.reason, requested_at = CURRENT_TIMESTAMP,
            attempts = 0, last_error = NULL;
    END IF;

    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END;
$$;

DROP TRIGGER IF EXISTS trg_work_schedule_day_recalculation ON public.work_schedule_days;
CREATE TRIGGER trg_work_schedule_day_recalculation
AFTER INSERT OR UPDATE OR DELETE ON public.work_schedule_days
FOR EACH ROW EXECUTE FUNCTION public.queue_attendance_from_schedule_version();

DROP TRIGGER IF EXISTS trg_work_schedule_version_recalculation ON public.work_schedule_versions;
CREATE TRIGGER trg_work_schedule_version_recalculation
AFTER INSERT OR UPDATE OR DELETE ON public.work_schedule_versions
FOR EACH ROW EXECUTE FUNCTION public.queue_attendance_from_schedule_version();

COMMIT;
