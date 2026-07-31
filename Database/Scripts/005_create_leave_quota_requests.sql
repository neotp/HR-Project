BEGIN;

CREATE TABLE IF NOT EXISTS public.leave_quota_requests
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_no          VARCHAR(30)  NOT NULL UNIQUE,
    employee_id         VARCHAR(50)  NOT NULL,
    leave_type_id       BIGINT       NOT NULL,
    quota_year          SMALLINT     NOT NULL,
    requested_hours     NUMERIC(8,2) NOT NULL,
    approved_hours      NUMERIC(8,2),
    request_reason      TEXT         NOT NULL,
    status              VARCHAR(30)  NOT NULL DEFAULT 'PENDING',
    requested_by        VARCHAR(50)  NOT NULL,
    requested_by_name   VARCHAR(200) NOT NULL,
    requested_at        TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    reviewed_by         VARCHAR(50),
    reviewed_by_name    VARCHAR(200),
    reviewed_at         TIMESTAMPTZ,
    review_remark       TEXT,
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_leave_quota_requests_leave_type
        FOREIGN KEY (leave_type_id) REFERENCES public.leave_types(id),
    CONSTRAINT ck_leave_quota_requests_year
        CHECK (quota_year BETWEEN 2000 AND 2200),
    CONSTRAINT ck_leave_quota_requests_hours
        CHECK (requested_hours > 0),
    CONSTRAINT ck_leave_quota_requests_approved_hours
        CHECK
        (
            approved_hours IS NULL
            OR (approved_hours >= 0 AND approved_hours <= requested_hours)
        ),
    CONSTRAINT ck_leave_quota_requests_status
        CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED'))
);

COMMENT ON COLUMN public.leave_quota_requests.employee_id IS
    'External employee key; add FK after the employee table is introduced.';

CREATE UNIQUE INDEX IF NOT EXISTS ux_leave_quota_requests_pending
    ON public.leave_quota_requests (employee_id, leave_type_id, quota_year)
    WHERE status = 'PENDING';

CREATE INDEX IF NOT EXISTS ix_leave_quota_requests_employee_year
    ON public.leave_quota_requests (employee_id, quota_year, requested_at DESC);

CREATE TABLE IF NOT EXISTS public.leave_quota_request_history
(
    id                      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_quota_request_id  BIGINT       NOT NULL,
    action                  VARCHAR(30)  NOT NULL,
    details_text            TEXT,
    action_by               VARCHAR(50)  NOT NULL,
    action_by_name          VARCHAR(200) NOT NULL,
    action_at               TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_leave_quota_request_history_request
        FOREIGN KEY (leave_quota_request_id)
        REFERENCES public.leave_quota_requests(id) ON DELETE CASCADE,
    CONSTRAINT ck_leave_quota_request_history_action
        CHECK (action IN ('CREATE_REQUEST', 'APPROVE', 'REJECT', 'CANCEL'))
);

CREATE INDEX IF NOT EXISTS ix_leave_quota_request_history_request
    ON public.leave_quota_request_history (leave_quota_request_id, action_at DESC);

DROP TRIGGER IF EXISTS trg_leave_quota_requests_updated_at ON public.leave_quota_requests;
CREATE TRIGGER trg_leave_quota_requests_updated_at
BEFORE UPDATE ON public.leave_quota_requests
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

COMMIT;
