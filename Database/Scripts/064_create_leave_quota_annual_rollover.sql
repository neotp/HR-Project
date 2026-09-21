BEGIN;

CREATE TABLE IF NOT EXISTS public.leave_quota_yearly_rollovers
(
    id                          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id                 VARCHAR(50)   NOT NULL,
    leave_type_id               BIGINT        NOT NULL REFERENCES public.leave_types(id),
    quota_year                  SMALLINT      NOT NULL,
    previous_remaining_hours    NUMERIC(10,2) NOT NULL DEFAULT 0,
    new_default_hours           NUMERIC(10,2) NOT NULL DEFAULT 0,
    opening_quota_hours         NUMERIC(10,2) NOT NULL DEFAULT 0,
    annual_excess_hours         NUMERIC(10,2) NOT NULL DEFAULT 0,
    processed_at                TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_at                  TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                  TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_leave_quota_yearly_rollovers
        UNIQUE (employee_id, leave_type_id, quota_year),
    CONSTRAINT ck_leave_quota_yearly_rollovers_year
        CHECK (quota_year BETWEEN 2000 AND 2200),
    CONSTRAINT ck_leave_quota_yearly_rollovers_hours
        CHECK
        (
            previous_remaining_hours >= 0 AND new_default_hours >= 0
            AND opening_quota_hours >= 0 AND annual_excess_hours >= 0
        )
);

CREATE TABLE IF NOT EXISTS public.leave_quota_excess_details
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id         VARCHAR(50)   NOT NULL,
    leave_type_id       BIGINT        NOT NULL REFERENCES public.leave_types(id),
    quota_year          SMALLINT      NOT NULL,
    source_type         VARCHAR(40)   NOT NULL,
    source_id           BIGINT        NOT NULL,
    source_year         SMALLINT,
    requested_hours     NUMERIC(10,2) NOT NULL,
    credited_hours      NUMERIC(10,2) NOT NULL DEFAULT 0,
    excess_hours        NUMERIC(10,2) NOT NULL DEFAULT 0,
    notes               TEXT,
    created_at          TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_leave_quota_excess_source UNIQUE (source_type, source_id),
    CONSTRAINT ck_leave_quota_excess_details_year
        CHECK (quota_year BETWEEN 2000 AND 2200),
    CONSTRAINT ck_leave_quota_excess_details_hours
        CHECK
        (
            requested_hours > 0 AND credited_hours >= 0 AND excess_hours >= 0
            AND credited_hours + excess_hours = requested_hours
        )
);

CREATE INDEX IF NOT EXISTS ix_leave_quota_rollovers_year_employee
    ON public.leave_quota_yearly_rollovers(quota_year, employee_id, leave_type_id);
CREATE INDEX IF NOT EXISTS ix_leave_quota_excess_year_employee
    ON public.leave_quota_excess_details(quota_year, employee_id, leave_type_id);

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order)
VALUES
    ('LEAVE_QUOTA_ANNUAL_REPORT', 'รายงานโควต้าวันลาประจำปี',
     '/leave/quota-annual-report', 'การลา', 67)
ON CONFLICT (page_key) DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

-- Intentionally do not grant this page to any employee or App Role.
-- It remains hidden until access is explicitly enabled on the permission page.

COMMIT;
