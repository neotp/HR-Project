BEGIN;

CREATE TABLE IF NOT EXISTS public.comm_groups
(
    id                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    comm_group_code   VARCHAR(50)  NOT NULL,
    comm_group_name   VARCHAR(200) NOT NULL,
    display_order     INTEGER      NOT NULL DEFAULT 0,
    is_active         BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at        TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at        TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_comm_groups_code_not_blank CHECK (BTRIM(comm_group_code) <> ''),
    CONSTRAINT ck_comm_groups_name_not_blank CHECK (BTRIM(comm_group_name) <> ''),
    CONSTRAINT ck_comm_groups_display_order CHECK (display_order >= 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_comm_groups_code_ci
    ON public.comm_groups (LOWER(BTRIM(comm_group_code)));

CREATE UNIQUE INDEX IF NOT EXISTS ux_comm_groups_name_ci
    ON public.comm_groups (LOWER(BTRIM(comm_group_name)));

CREATE INDEX IF NOT EXISTS ix_comm_groups_active_order
    ON public.comm_groups (is_active, display_order, comm_group_name);

DROP TRIGGER IF EXISTS trg_comm_groups_updated_at ON public.comm_groups;
CREATE TRIGGER trg_comm_groups_updated_at
BEFORE UPDATE ON public.comm_groups
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

CREATE TABLE IF NOT EXISTS public.employee_comm_groups
(
    employee_id     BIGINT      NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    comm_group_id   BIGINT      NOT NULL REFERENCES public.comm_groups(id) ON DELETE RESTRICT,
    display_order   INTEGER     NOT NULL DEFAULT 1,
    assigned_at     TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    assigned_by     VARCHAR(100),
    PRIMARY KEY (employee_id, comm_group_id),
    CONSTRAINT ck_employee_comm_groups_display_order CHECK (display_order >= 1)
);

CREATE INDEX IF NOT EXISTS ix_employee_comm_groups_group_employee
    ON public.employee_comm_groups (comm_group_id, employee_id);

COMMENT ON TABLE public.comm_groups IS
    'Communication Group master data.';
COMMENT ON TABLE public.employee_comm_groups IS
    'Many-to-many relation between employees and their Communication Groups.';

COMMIT;
