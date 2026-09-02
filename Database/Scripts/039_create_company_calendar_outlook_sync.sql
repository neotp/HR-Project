BEGIN;

CREATE TABLE IF NOT EXISTS public.work_calendar_outlook_events
(
    id                    BIGSERIAL PRIMARY KEY,
    work_calendar_day_id  BIGINT REFERENCES public.work_calendar_days(id) ON DELETE SET NULL,
    employee_id           VARCHAR(50) NOT NULL,
    employee_email        VARCHAR(320) NOT NULL,
    event_mailbox_email   VARCHAR(320),
    calendar_date         DATE NOT NULL,
    day_type              VARCHAR(30) NOT NULL,
    event_name            VARCHAR(250) NOT NULL,
    event_note            TEXT,
    outlook_event_id      TEXT,
    outlook_web_link      TEXT,
    transaction_id        UUID NOT NULL DEFAULT gen_random_uuid(),
    desired_action        VARCHAR(10) NOT NULL DEFAULT 'UPSERT',
    sync_status           VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    retry_count           INTEGER NOT NULL DEFAULT 0,
    last_sync_error       TEXT,
    last_attempted_at     TIMESTAMPTZ,
    synced_at             TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT ux_work_calendar_outlook_date_employee UNIQUE (calendar_date, employee_id),
    CONSTRAINT ux_work_calendar_outlook_transaction UNIQUE (transaction_id),
    CONSTRAINT ck_work_calendar_outlook_action CHECK (desired_action IN ('UPSERT', 'DELETE')),
    CONSTRAINT ck_work_calendar_outlook_status CHECK (sync_status IN ('PENDING', 'SYNCED', 'FAILED', 'DELETED')),
    CONSTRAINT ck_work_calendar_outlook_retry CHECK (retry_count >= 0)
);

CREATE INDEX IF NOT EXISTS ix_work_calendar_outlook_retry
    ON public.work_calendar_outlook_events(sync_status, last_attempted_at, id)
    WHERE sync_status IN ('PENDING', 'FAILED');

CREATE OR REPLACE FUNCTION public.queue_work_calendar_outlook_upsert()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    -- Keep the existing Outlook Event IDs when the calendar date changes so
    -- Microsoft Graph can move the same events with PATCH instead of creating duplicates.
    UPDATE public.work_calendar_outlook_events
    SET calendar_date = NEW.calendar_date,
        employee_email = COALESCE(NULLIF(BTRIM(basic.email_address), ''), employee_email),
        day_type = NEW.day_type,
        event_name = NEW.name,
        event_note = NEW.note,
        desired_action = 'UPSERT',
        sync_status = 'PENDING',
        retry_count = 0,
        last_sync_error = NULL,
        updated_at = CURRENT_TIMESTAMP
    FROM public.employees employee
    LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
    WHERE public.work_calendar_outlook_events.work_calendar_day_id = NEW.id
      AND employee.employee_code = public.work_calendar_outlook_events.employee_id;

    INSERT INTO public.work_calendar_outlook_events
        (work_calendar_day_id, employee_id, employee_email,
         calendar_date, day_type, event_name, event_note,
         desired_action, sync_status)
    SELECT NEW.id, employee.employee_code, BTRIM(basic.email_address),
           NEW.calendar_date, NEW.day_type, NEW.name, NEW.note,
           'UPSERT', 'PENDING'
    FROM public.employees employee
    JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
    WHERE employee.is_active = TRUE
      AND NULLIF(BTRIM(basic.email_address), '') IS NOT NULL
      AND BTRIM(basic.email_address) LIKE '%@%'
    ON CONFLICT (calendar_date, employee_id) DO UPDATE SET
        work_calendar_day_id = EXCLUDED.work_calendar_day_id,
        employee_email = EXCLUDED.employee_email,
        day_type = EXCLUDED.day_type,
        event_name = EXCLUDED.event_name,
        event_note = EXCLUDED.event_note,
        desired_action = 'UPSERT',
        sync_status = 'PENDING',
        retry_count = 0,
        last_sync_error = NULL,
        updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION public.queue_work_calendar_outlook_delete()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE public.work_calendar_outlook_events
    SET work_calendar_day_id = NULL,
        desired_action = 'DELETE',
        sync_status = 'PENDING',
        retry_count = 0,
        last_sync_error = NULL,
        updated_at = CURRENT_TIMESTAMP
    WHERE calendar_date = OLD.calendar_date;
    RETURN OLD;
END;
$$;

DROP TRIGGER IF EXISTS trg_work_calendar_outlook_upsert ON public.work_calendar_days;
CREATE TRIGGER trg_work_calendar_outlook_upsert
AFTER INSERT OR UPDATE OF calendar_date, day_type, name, note ON public.work_calendar_days
FOR EACH ROW EXECUTE FUNCTION public.queue_work_calendar_outlook_upsert();

DROP TRIGGER IF EXISTS trg_work_calendar_outlook_delete ON public.work_calendar_days;
CREATE TRIGGER trg_work_calendar_outlook_delete
BEFORE DELETE ON public.work_calendar_days
FOR EACH ROW EXECUTE FUNCTION public.queue_work_calendar_outlook_delete();

-- Seed existing company calendar entries for every active employee mailbox.
INSERT INTO public.work_calendar_outlook_events
    (work_calendar_day_id, employee_id, employee_email,
     calendar_date, day_type, event_name, event_note,
     desired_action, sync_status)
SELECT day.id, employee.employee_code, BTRIM(basic.email_address),
       day.calendar_date, day.day_type, day.name, day.note,
       'UPSERT', 'PENDING'
FROM public.work_calendar_days day
CROSS JOIN public.employees employee
JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
WHERE employee.is_active = TRUE
  AND day.calendar_date >= CURRENT_DATE
  AND NULLIF(BTRIM(basic.email_address), '') IS NOT NULL
  AND BTRIM(basic.email_address) LIKE '%@%'
ON CONFLICT (calendar_date, employee_id) DO UPDATE SET
    work_calendar_day_id = EXCLUDED.work_calendar_day_id,
    employee_email = EXCLUDED.employee_email,
    day_type = EXCLUDED.day_type,
    event_name = EXCLUDED.event_name,
    event_note = EXCLUDED.event_note,
    desired_action = 'UPSERT',
    sync_status = CASE
        WHEN public.work_calendar_outlook_events.outlook_event_id IS NULL THEN 'PENDING'
        ELSE public.work_calendar_outlook_events.sync_status
    END,
    updated_at = CURRENT_TIMESTAMP;

COMMIT;
