BEGIN;

CREATE SCHEMA IF NOT EXISTS public;

-- Keep this migration runnable even when the leave migration that originally
-- introduced the shared updated_at function has not been applied.
CREATE OR REPLACE FUNCTION public.set_updated_at()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$;

CREATE TABLE IF NOT EXISTS public.work_calendar_days
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    calendar_date   DATE         NOT NULL,
    day_type        VARCHAR(30)  NOT NULL,
    name            VARCHAR(200) NOT NULL,
    note            TEXT,
    created_by      VARCHAR(50)  NOT NULL,
    created_by_name VARCHAR(200) NOT NULL,
    updated_by      VARCHAR(50)  NOT NULL,
    updated_by_name VARCHAR(200) NOT NULL,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT ux_work_calendar_days_date UNIQUE (calendar_date),
    CONSTRAINT ck_work_calendar_days_type
        CHECK (day_type IN ('PUBLIC_HOLIDAY', 'WORKING_SATURDAY')),
    CONSTRAINT ck_work_calendar_working_saturday
        CHECK (
            day_type <> 'WORKING_SATURDAY'
            OR EXTRACT(DOW FROM calendar_date) = 6
        )
);

CREATE INDEX IF NOT EXISTS ix_work_calendar_days_type_date
    ON public.work_calendar_days(day_type, calendar_date);

DROP TRIGGER IF EXISTS trg_work_calendar_days_updated_at
    ON public.work_calendar_days;
CREATE TRIGGER trg_work_calendar_days_updated_at
    BEFORE UPDATE ON public.work_calendar_days
    FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

-- Page permissions were introduced in migration 007. Do not fail the calendar
-- migration when that optional module has not been installed yet.
DO $$
BEGIN
    IF to_regclass('public.application_pages') IS NOT NULL THEN
        INSERT INTO public.application_pages
            (page_key, page_name, route_path, category_name, display_order)
        VALUES
            ('WORK_CALENDAR', 'กำหนดปฏิทินการทำงาน',
             '/attendance/calendar-settings', 'พนักงาน', 90)
        ON CONFLICT (page_key)
        DO UPDATE SET
            page_name = EXCLUDED.page_name,
            route_path = EXCLUDED.route_path,
            category_name = EXCLUDED.category_name,
            display_order = EXCLUDED.display_order,
            is_active = TRUE;

        UPDATE public.application_pages
        SET display_order = 100
        WHERE page_key = 'PERMISSIONS';
    END IF;
END;
$$;

COMMIT;
