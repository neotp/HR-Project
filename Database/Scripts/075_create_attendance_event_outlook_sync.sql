BEGIN;

CREATE TABLE IF NOT EXISTS public.attendance_event_outlook_sync
(
    id                            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_event_id               BIGINT NOT NULL UNIQUE,
    attendance_calendar_event_id  BIGINT REFERENCES public.attendance_calendar_events(id) ON DELETE SET NULL,
    employee_id                   VARCHAR(50) NOT NULL,
    employee_email                VARCHAR(320),
    event_date                    DATE NOT NULL,
    start_time                    TIME NOT NULL,
    end_time                      TIME NOT NULL,
    event_type                    VARCHAR(60) NOT NULL,
    event_type_name               VARCHAR(200) NOT NULL,
    event_title                   VARCHAR(200),
    event_details                 TEXT,
    review_status                 VARCHAR(30) NOT NULL,
    desired_action                VARCHAR(10) NOT NULL DEFAULT 'UPSERT',
    sync_status                   VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    event_mailbox_email           VARCHAR(320),
    outlook_event_id              TEXT,
    outlook_web_link              TEXT,
    transaction_id                UUID NOT NULL DEFAULT gen_random_uuid(),
    retry_count                   INTEGER NOT NULL DEFAULT 0,
    last_sync_error               TEXT,
    last_attempted_at             TIMESTAMPTZ,
    synced_at                     TIMESTAMPTZ,
    created_at                    TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                    TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_attendance_event_outlook_action CHECK (desired_action IN ('UPSERT', 'DELETE')),
    CONSTRAINT ck_attendance_event_outlook_status CHECK (sync_status IN ('PENDING', 'SYNCED', 'FAILED', 'DELETED')),
    CONSTRAINT ck_attendance_event_outlook_retry CHECK (retry_count >= 0)
);

CREATE INDEX IF NOT EXISTS ix_attendance_event_outlook_retry
    ON public.attendance_event_outlook_sync(sync_status, last_attempted_at, id)
    WHERE sync_status IN ('PENDING', 'FAILED');

CREATE OR REPLACE FUNCTION public.queue_attendance_event_outlook_upsert()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    mailbox VARCHAR(320);
    type_name VARCHAR(200);
BEGIN
    SELECT NULLIF(BTRIM(basic.email_address), '')
      INTO mailbox
      FROM public.employees employee
      LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
     WHERE employee.employee_code = NEW.employee_id
     LIMIT 1;

    SELECT COALESCE(NULLIF(BTRIM(master.name_th), ''), NEW.event_type)
      INTO type_name
      FROM public.attendance_event_types master
     WHERE master.code = NEW.event_type
     LIMIT 1;
    type_name := COALESCE(type_name, NEW.event_type);

    INSERT INTO public.attendance_event_outlook_sync
        (source_event_id, attendance_calendar_event_id, employee_id, employee_email,
         event_date, start_time, end_time, event_type, event_type_name,
         event_title, event_details, review_status, desired_action, sync_status)
    VALUES
        (NEW.id, NEW.id, NEW.employee_id, mailbox,
         NEW.event_date, NEW.start_time, NEW.end_time, NEW.event_type, type_name,
         NEW.title, NEW.details, NEW.status,
         CASE WHEN NEW.status = 'REJECTED' THEN 'DELETE' ELSE 'UPSERT' END,
         'PENDING')
    ON CONFLICT (source_event_id) DO UPDATE SET
        attendance_calendar_event_id = NEW.id,
        employee_id = NEW.employee_id,
        employee_email = mailbox,
        event_date = NEW.event_date,
        start_time = NEW.start_time,
        end_time = NEW.end_time,
        event_type = NEW.event_type,
        event_type_name = type_name,
        event_title = NEW.title,
        event_details = NEW.details,
        review_status = NEW.status,
        desired_action = CASE WHEN NEW.status = 'REJECTED' THEN 'DELETE' ELSE 'UPSERT' END,
        sync_status = 'PENDING',
        retry_count = 0,
        last_sync_error = NULL,
        updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION public.queue_attendance_event_outlook_delete()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE public.attendance_event_outlook_sync
       SET desired_action = 'DELETE', sync_status = 'PENDING', retry_count = 0,
           last_sync_error = NULL, updated_at = CURRENT_TIMESTAMP
     WHERE source_event_id = OLD.id;
    RETURN OLD;
END;
$$;

DROP TRIGGER IF EXISTS trg_attendance_event_outlook_upsert ON public.attendance_calendar_events;
CREATE TRIGGER trg_attendance_event_outlook_upsert
AFTER INSERT OR UPDATE OF event_date, event_type, start_time, end_time, title, details, status
ON public.attendance_calendar_events
FOR EACH ROW EXECUTE FUNCTION public.queue_attendance_event_outlook_upsert();

DROP TRIGGER IF EXISTS trg_attendance_event_outlook_delete ON public.attendance_calendar_events;
CREATE TRIGGER trg_attendance_event_outlook_delete
BEFORE DELETE ON public.attendance_calendar_events
FOR EACH ROW EXECUTE FUNCTION public.queue_attendance_event_outlook_delete();

-- Queue existing current/future events without creating historical calendar noise.
INSERT INTO public.attendance_event_outlook_sync
    (source_event_id, attendance_calendar_event_id, employee_id, employee_email,
     event_date, start_time, end_time, event_type, event_type_name,
     event_title, event_details, review_status, desired_action, sync_status)
SELECT event.id, event.id, event.employee_id, NULLIF(BTRIM(basic.email_address), ''),
       event.event_date, event.start_time, event.end_time, event.event_type,
       COALESCE(NULLIF(BTRIM(master.name_th), ''), event.event_type),
       event.title, event.details, event.status,
       CASE WHEN event.status = 'REJECTED' THEN 'DELETE' ELSE 'UPSERT' END,
       'PENDING'
FROM public.attendance_calendar_events event
LEFT JOIN public.employees employee ON employee.employee_code = event.employee_id
LEFT JOIN public.employee_basic_info basic ON basic.employee_id = employee.id
LEFT JOIN public.attendance_event_types master ON master.code = event.event_type
WHERE event.event_date >= CURRENT_DATE
ON CONFLICT (source_event_id) DO NOTHING;

COMMIT;
