CREATE TABLE IF NOT EXISTS public.manager_leave_notifications
(
    id                    BIGSERIAL PRIMARY KEY,
    notification_no       VARCHAR(30) NOT NULL UNIQUE,
    sender_employee_id    BIGINT NOT NULL REFERENCES public.employees(id),
    recipient_employee_id BIGINT NOT NULL REFERENCES public.employees(id),
    sender_name            VARCHAR(300) NOT NULL,
    sender_email           VARCHAR(320) NOT NULL,
    recipient_name         VARCHAR(300) NOT NULL,
    recipient_email        VARCHAR(320) NOT NULL,
    subject                VARCHAR(300) NOT NULL,
    start_date             DATE NOT NULL,
    end_date               DATE NOT NULL,
    start_time             TIME NULL,
    end_time               TIME NULL,
    leave_hours            NUMERIC(8,2) NULL,
    details                TEXT NOT NULL,
    email_status           VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    email_error            TEXT NULL,
    sent_at                TIMESTAMPTZ NULL,
    created_at             TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_manager_leave_notification_dates CHECK (end_date >= start_date),
    CONSTRAINT ck_manager_leave_notification_hours
        CHECK (leave_hours IS NULL OR (leave_hours > 0 AND leave_hours <= 24)),
    CONSTRAINT ck_manager_leave_notification_status
        CHECK (email_status IN ('PENDING', 'SENT', 'FAILED'))
);

CREATE INDEX IF NOT EXISTS ix_manager_leave_notifications_sender_created
    ON public.manager_leave_notifications(sender_employee_id, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_manager_leave_notifications_recipient_created
    ON public.manager_leave_notifications(recipient_employee_id, created_at DESC);

COMMENT ON TABLE public.manager_leave_notifications IS
    'ประวัติการแจ้งข้อมูลการลาจากหัวหน้าทางอีเมล ไม่ใช่เอกสารลาและไม่มีผลต่อโควตา';
