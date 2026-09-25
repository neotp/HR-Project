BEGIN;

-- Wi-Fi start_time is a timestamptz. Keep the original instant so it can be
-- compared with a camera scan after converting to the attendance time zone.
CREATE TABLE IF NOT EXISTS public.attendance_wifi_connections
(
    id                 BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id        VARCHAR(50) NOT NULL,
    mac_address        VARCHAR(12) NOT NULL,
    source_session_id  TEXT,
    start_at           TIMESTAMPTZ NOT NULL,
    imported_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_attendance_wifi_connection
        UNIQUE NULLS NOT DISTINCT (employee_id, mac_address, start_at, source_session_id)
);

CREATE INDEX IF NOT EXISTS ix_attendance_wifi_employee_start
    ON public.attendance_wifi_connections(employee_id, start_at);

CREATE TABLE IF NOT EXISTS public.attendance_wifi_sync_state
(
    source_system   VARCHAR(50) PRIMARY KEY,
    source_schema   VARCHAR(128),
    source_table    VARCHAR(128),
    last_window_end TIMESTAMPTZ,
    last_success_at TIMESTAMPTZ,
    last_error      TEXT,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

INSERT INTO public.attendance_wifi_sync_state(source_system)
VALUES ('WIFI')
ON CONFLICT (source_system) DO NOTHING;

COMMIT;
