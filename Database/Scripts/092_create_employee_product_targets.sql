BEGIN;

CREATE TABLE IF NOT EXISTS public.product_business_units
(
    id                         BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    product_business_unit_code VARCHAR(50)  NOT NULL,
    product_business_unit_name VARCHAR(200) NOT NULL,
    display_order              INTEGER      NOT NULL DEFAULT 0,
    is_active                  BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at                 TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                 TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_product_business_units_code_not_blank
        CHECK (BTRIM(product_business_unit_code) <> ''),
    CONSTRAINT ck_product_business_units_name_not_blank
        CHECK (BTRIM(product_business_unit_name) <> ''),
    CONSTRAINT ck_product_business_units_display_order CHECK (display_order >= 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_product_business_units_code_ci
    ON public.product_business_units (LOWER(BTRIM(product_business_unit_code)));
CREATE UNIQUE INDEX IF NOT EXISTS ux_product_business_units_name_ci
    ON public.product_business_units (LOWER(BTRIM(product_business_unit_name)));
CREATE INDEX IF NOT EXISTS ix_product_business_units_active_order
    ON public.product_business_units (is_active, display_order, product_business_unit_name);

DROP TRIGGER IF EXISTS trg_product_business_units_updated_at ON public.product_business_units;
CREATE TRIGGER trg_product_business_units_updated_at
BEFORE UPDATE ON public.product_business_units
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

-- Product BU, Brand and Comm.Group are one assignment tuple.  Keeping them in
-- one row prevents a search from matching values that belong to different targets.
CREATE TABLE IF NOT EXISTS public.employee_product_targets
(
    id                       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    period_code              VARCHAR(20)  NOT NULL,
    employee_id              BIGINT       NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    product_business_unit_id BIGINT       NOT NULL REFERENCES public.product_business_units(id) ON DELETE RESTRICT,
    brand_id                 BIGINT       NOT NULL REFERENCES public.brands(id) ON DELETE RESTRICT,
    comm_group_id            BIGINT       NOT NULL REFERENCES public.comm_groups(id) ON DELETE RESTRICT,
    department_snapshot      VARCHAR(200),
    is_current               BOOLEAN      NOT NULL DEFAULT TRUE,
    assigned_at              TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    assigned_by              VARCHAR(100),
    CONSTRAINT ck_employee_product_targets_period_not_blank CHECK (BTRIM(period_code) <> ''),
    CONSTRAINT ux_employee_product_targets_assignment
        UNIQUE (period_code, employee_id, product_business_unit_id, brand_id, comm_group_id)
);

CREATE INDEX IF NOT EXISTS ix_employee_product_targets_current_employee
    ON public.employee_product_targets (employee_id) WHERE is_current = TRUE;
CREATE INDEX IF NOT EXISTS ix_employee_product_targets_current_facets
    ON public.employee_product_targets
       (product_business_unit_id, brand_id, comm_group_id, employee_id)
    WHERE is_current = TRUE;

COMMENT ON TABLE public.product_business_units IS
    'Product BU master; independent from the employee organizational BU.';
COMMENT ON TABLE public.employee_product_targets IS
    'Period-aware employee Product BU, Brand and Comm.Group target assignments.';

COMMIT;
