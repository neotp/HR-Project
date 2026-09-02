BEGIN;

CREATE TABLE IF NOT EXISTS public.attendance_raw_scans
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_system       VARCHAR(50)  NOT NULL DEFAULT 'HIKVISION',
    source_employee_id  VARCHAR(50)  NOT NULL,
    captured_at         TIMESTAMP    NOT NULL,
    source_status       VARCHAR(50),
    device_name         VARCHAR(200),
    device_no           VARCHAR(300),
    source_payload      JSONB,
    imported_at         TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_attendance_raw_scan
        UNIQUE NULLS NOT DISTINCT
        (source_system, source_employee_id, captured_at, device_no)
);

CREATE INDEX IF NOT EXISTS ix_attendance_raw_scans_employee_time
    ON public.attendance_raw_scans(source_employee_id, captured_at);
CREATE INDEX IF NOT EXISTS ix_attendance_raw_scans_captured_at
    ON public.attendance_raw_scans(captured_at);

CREATE TABLE IF NOT EXISTS public.attendance_sync_states
(
    source_system       VARCHAR(50) PRIMARY KEY,
    source_schema       VARCHAR(128),
    source_table        VARCHAR(128),
    last_captured_at    TIMESTAMP,
    last_success_at     TIMESTAMPTZ,
    last_error          TEXT,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

INSERT INTO public.attendance_sync_states(source_system)
VALUES ('HIKVISION')
ON CONFLICT (source_system) DO NOTHING;

CREATE TABLE IF NOT EXISTS public.attendance_daily_records
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id         VARCHAR(50) NOT NULL,
    work_date           DATE        NOT NULL,
    first_scan_at       TIMESTAMP,
    last_scan_at        TIMESTAMP,
    scan_count          INTEGER     NOT NULL DEFAULT 0,
    calculated_status   VARCHAR(30) NOT NULL,
    final_status        VARCHAR(30) NOT NULL,
    late_minutes        INTEGER     NOT NULL DEFAULT 0,
    missing_minutes     INTEGER     NOT NULL DEFAULT 0,
    requires_review     BOOLEAN     NOT NULL DEFAULT FALSE,
    review_reason       TEXT,
    calculation_detail JSONB,
    calculation_version VARCHAR(30) NOT NULL DEFAULT 'V1',
    calculated_at       TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    overridden_by       VARCHAR(50),
    overridden_by_name  VARCHAR(200),
    overridden_at       TIMESTAMPTZ,
    override_reason     TEXT,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_attendance_daily_employee_date UNIQUE(employee_id, work_date),
    CONSTRAINT ck_attendance_daily_status CHECK
    (
        calculated_status IN
        ('PRESENT','LATE','ABSENT','INCOMPLETE','REVIEW_REQUIRED','NO_DATA','IN_PROGRESS','LEAVE')
        AND final_status IN
        ('PRESENT','LATE','ABSENT','INCOMPLETE','REVIEW_REQUIRED','NO_DATA','IN_PROGRESS','LEAVE')
    ),
    CONSTRAINT ck_attendance_daily_counts CHECK
        (scan_count >= 0 AND late_minutes >= 0 AND missing_minutes >= 0)
);

CREATE INDEX IF NOT EXISTS ix_attendance_daily_employee_date
    ON public.attendance_daily_records(employee_id, work_date DESC);
CREATE INDEX IF NOT EXISTS ix_attendance_daily_review
    ON public.attendance_daily_records(work_date DESC, final_status)
    WHERE requires_review = TRUE;

CREATE TABLE IF NOT EXISTS public.attendance_daily_history
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    attendance_daily_id BIGINT       NOT NULL
        REFERENCES public.attendance_daily_records(id) ON DELETE CASCADE,
    action              VARCHAR(40)  NOT NULL,
    status_before       VARCHAR(30),
    status_after        VARCHAR(30)  NOT NULL,
    details             TEXT,
    details_json        JSONB,
    action_by           VARCHAR(50)  NOT NULL,
    action_by_name      VARCHAR(200) NOT NULL,
    action_at           TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_attendance_history_action CHECK
        (action IN ('AUTO_CALCULATE','AUTO_RECALCULATE','MANUAL_OVERRIDE','RESET_OVERRIDE'))
);

CREATE INDEX IF NOT EXISTS ix_attendance_daily_history_record_time
    ON public.attendance_daily_history(attendance_daily_id, action_at DESC, id DESC);

COMMIT;
