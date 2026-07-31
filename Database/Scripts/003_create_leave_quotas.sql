BEGIN;

CREATE TABLE IF NOT EXISTS public.leave_quotas
(
    id                      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id             VARCHAR(50)  NOT NULL,
    leave_type_id           BIGINT       NOT NULL,
    quota_year              SMALLINT     NOT NULL,
    quota_hours             NUMERIC(8,2) NOT NULL DEFAULT 0,
    used_hours              NUMERIC(8,2) NOT NULL DEFAULT 0,
    notes                   TEXT,
    created_by              VARCHAR(50)  NOT NULL,
    created_by_name         VARCHAR(200) NOT NULL,
    created_at              TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_by              VARCHAR(50)  NOT NULL,
    updated_by_name         VARCHAR(200) NOT NULL,
    updated_at              TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_leave_quotas_leave_type
        FOREIGN KEY (leave_type_id) REFERENCES public.leave_types(id),
    CONSTRAINT ux_leave_quotas_employee_type_year
        UNIQUE (employee_id, leave_type_id, quota_year),
    CONSTRAINT ck_leave_quotas_year
        CHECK (quota_year BETWEEN 2000 AND 2200),
    CONSTRAINT ck_leave_quotas_hours
        CHECK
        (
            quota_hours >= 0
            AND used_hours >= 0
            AND used_hours <= quota_hours
        )
);

-- employee_id will receive a foreign key when the real employee table and
-- its key type are available. It intentionally remains an external key now.
COMMENT ON COLUMN public.leave_quotas.employee_id IS
    'External employee key; add FK after the employee table is introduced.';

CREATE TABLE IF NOT EXISTS public.leave_quota_history
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_quota_id      BIGINT       NOT NULL,
    action              VARCHAR(20)  NOT NULL,
    details_text        TEXT,
    before_data         JSONB,
    after_data          JSONB,
    action_by           VARCHAR(50)  NOT NULL,
    action_by_name      VARCHAR(200) NOT NULL,
    action_at           TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_leave_quota_history_quota
        FOREIGN KEY (leave_quota_id)
        REFERENCES public.leave_quotas(id) ON DELETE CASCADE,
    CONSTRAINT ck_leave_quota_history_action
        CHECK (action IN ('CREATE', 'UPDATE'))
);

CREATE INDEX IF NOT EXISTS ix_leave_quotas_employee_year
    ON public.leave_quotas (employee_id, quota_year);

CREATE INDEX IF NOT EXISTS ix_leave_quotas_year_type
    ON public.leave_quotas (quota_year, leave_type_id);

CREATE INDEX IF NOT EXISTS ix_leave_quota_history_quota_action_at
    ON public.leave_quota_history (leave_quota_id, action_at DESC);

DROP TRIGGER IF EXISTS trg_leave_quotas_updated_at ON public.leave_quotas;
CREATE TRIGGER trg_leave_quotas_updated_at
BEFORE UPDATE ON public.leave_quotas
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

COMMIT;
