BEGIN;

CREATE TABLE IF NOT EXISTS public.application_pages
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    page_key        VARCHAR(80)  NOT NULL UNIQUE,
    page_name       VARCHAR(200) NOT NULL,
    route_path      VARCHAR(250) NOT NULL,
    category_name   VARCHAR(100) NOT NULL,
    display_order   INTEGER      NOT NULL DEFAULT 0,
    is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS public.employee_page_permissions
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id         VARCHAR(50) NOT NULL,
    application_page_id BIGINT      NOT NULL,
    can_access          BOOLEAN     NOT NULL DEFAULT FALSE,
    updated_by          VARCHAR(50) NOT NULL,
    updated_by_name     VARCHAR(200) NOT NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_employee_page_permissions_page
        FOREIGN KEY (application_page_id)
        REFERENCES public.application_pages(id)
        ON DELETE CASCADE,
    CONSTRAINT ux_employee_page_permissions
        UNIQUE (employee_id, application_page_id)
);

COMMENT ON COLUMN public.employee_page_permissions.employee_id IS
    'External employee key; add FK after the employee table is introduced.';

CREATE INDEX IF NOT EXISTS ix_employee_page_permissions_employee
    ON public.employee_page_permissions(employee_id);

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order)
VALUES
    ('LEAVE_DOCUMENTS', 'เอกสารการลา', '/leave/documents', 'การลา', 10),
    ('LEAVE_PENDING', 'เอกสารรออนุมัติ', '/leave/pending', 'การลา', 20),
    ('LEAVE_REVISIONS', 'ขอแก้ไขเอกสารการลา', '/leave/revisions', 'การลา', 30),
    ('LEAVE_TEAM', 'หัวหน้าแจ้งลาลูกน้อง', '/leave/team', 'การลา', 40),
    ('LEAVE_REQUEST_QUOTA', 'ขอวันลาเพิ่ม', '/leave/request-quota', 'การลา', 50),
    ('LEAVE_MANAGE_QUOTA', 'จัดการโควต้าวันลา', '/leave/manage-quota', 'การลา', 60),
    ('EMPLOYEES', 'ข้อมูลพนักงาน', '/', 'พนักงาน', 70),
    ('ATTENDANCE', 'การมาทำงาน', '/attendance', 'พนักงาน', 80),
    ('PERMISSIONS', 'จัดการสิทธิ์การเข้าถึง', '/permissions', 'การจัดการสิทธิ์', 90)
ON CONFLICT (page_key)
DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

DROP TRIGGER IF EXISTS trg_application_pages_updated_at
    ON public.application_pages;
CREATE TRIGGER trg_application_pages_updated_at
    BEFORE UPDATE ON public.application_pages
    FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

DROP TRIGGER IF EXISTS trg_employee_page_permissions_updated_at
    ON public.employee_page_permissions;
CREATE TRIGGER trg_employee_page_permissions_updated_at
    BEFORE UPDATE ON public.employee_page_permissions
    FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

COMMIT;
