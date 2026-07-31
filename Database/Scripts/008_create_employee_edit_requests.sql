BEGIN;

CREATE TABLE IF NOT EXISTS public.employee_edit_requests
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_no          VARCHAR(30)  NOT NULL UNIQUE,
    employee_id         VARCHAR(50)  NOT NULL,
    employee_name       VARCHAR(200) NOT NULL,
    changes_json        JSONB        NOT NULL,
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

    CONSTRAINT ck_employee_edit_requests_changes
        CHECK
        (
            jsonb_typeof(changes_json) = 'array'
            AND jsonb_array_length(changes_json) > 0
        ),
    CONSTRAINT ck_employee_edit_requests_status
        CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED'))
);

COMMENT ON COLUMN public.employee_edit_requests.employee_id IS
    'External employee key; add FK after the employee table is introduced.';

CREATE UNIQUE INDEX IF NOT EXISTS ux_employee_edit_requests_pending
    ON public.employee_edit_requests(employee_id)
    WHERE status = 'PENDING';

CREATE TABLE IF NOT EXISTS public.employee_edit_request_history
(
    id                          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_edit_request_id    BIGINT       NOT NULL,
    action                      VARCHAR(50)  NOT NULL,
    details_text                TEXT         NOT NULL,
    action_by                   VARCHAR(50)  NOT NULL,
    action_by_name              VARCHAR(200) NOT NULL,
    action_at                   TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_employee_edit_request_history_request
        FOREIGN KEY (employee_edit_request_id)
        REFERENCES public.employee_edit_requests(id)
        ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS ix_employee_edit_request_history_request
    ON public.employee_edit_request_history(employee_edit_request_id, action_at);

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order)
VALUES
    ('EMPLOYEE_EDIT_REQUESTS', 'ขอแก้ไขข้อมูลพนักงาน',
     '/employees/edit-requests', 'พนักงาน', 75)
ON CONFLICT (page_key)
DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

DROP TRIGGER IF EXISTS trg_employee_edit_requests_updated_at
    ON public.employee_edit_requests;
CREATE TRIGGER trg_employee_edit_requests_updated_at
    BEFORE UPDATE ON public.employee_edit_requests
    FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

COMMIT;
