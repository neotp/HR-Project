BEGIN;

CREATE TABLE IF NOT EXISTS public.app_roles
(
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    role_key    VARCHAR(80)  NOT NULL UNIQUE,
    role_name   VARCHAR(200) NOT NULL,
    description TEXT,
    is_active   BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS public.app_role_members
(
    app_role_id BIGINT      NOT NULL REFERENCES public.app_roles(id) ON DELETE CASCADE,
    employee_id VARCHAR(50) NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (app_role_id, employee_id)
);

CREATE INDEX IF NOT EXISTS ix_app_role_members_employee
    ON public.app_role_members(employee_id);

CREATE TABLE IF NOT EXISTS public.app_role_page_permissions
(
    app_role_id        BIGINT      NOT NULL REFERENCES public.app_roles(id) ON DELETE CASCADE,
    application_page_id BIGINT     NOT NULL REFERENCES public.application_pages(id) ON DELETE CASCADE,
    can_access         BOOLEAN     NOT NULL DEFAULT FALSE,
    updated_by         VARCHAR(50) NOT NULL,
    updated_by_name    VARCHAR(200) NOT NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (app_role_id, application_page_id)
);

CREATE TABLE IF NOT EXISTS public.app_role_page_action_permissions
(
    app_role_id                 BIGINT      NOT NULL REFERENCES public.app_roles(id) ON DELETE CASCADE,
    application_page_action_id  BIGINT      NOT NULL REFERENCES public.application_page_actions(id) ON DELETE CASCADE,
    can_execute                 BOOLEAN     NOT NULL DEFAULT FALSE,
    updated_by                  VARCHAR(50) NOT NULL,
    updated_by_name             VARCHAR(200) NOT NULL,
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (app_role_id, application_page_action_id)
);

DROP TRIGGER IF EXISTS trg_app_roles_updated_at ON public.app_roles;
CREATE TRIGGER trg_app_roles_updated_at
BEFORE UPDATE ON public.app_roles
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

DROP TRIGGER IF EXISTS trg_app_role_page_permissions_updated_at ON public.app_role_page_permissions;
CREATE TRIGGER trg_app_role_page_permissions_updated_at
BEFORE UPDATE ON public.app_role_page_permissions
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

DROP TRIGGER IF EXISTS trg_app_role_page_action_permissions_updated_at ON public.app_role_page_action_permissions;
CREATE TRIGGER trg_app_role_page_action_permissions_updated_at
BEFORE UPDATE ON public.app_role_page_action_permissions
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

COMMIT;
