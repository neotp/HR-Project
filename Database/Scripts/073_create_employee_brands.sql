BEGIN;

CREATE TABLE IF NOT EXISTS public.brands
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    brand_code      VARCHAR(50)  NOT NULL,
    brand_name      VARCHAR(200) NOT NULL,
    display_order   INTEGER      NOT NULL DEFAULT 0,
    is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_brands_code_not_blank CHECK (BTRIM(brand_code) <> ''),
    CONSTRAINT ck_brands_name_not_blank CHECK (BTRIM(brand_name) <> ''),
    CONSTRAINT ck_brands_display_order CHECK (display_order >= 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_brands_code_ci
    ON public.brands (LOWER(BTRIM(brand_code)));

CREATE UNIQUE INDEX IF NOT EXISTS ux_brands_name_ci
    ON public.brands (LOWER(BTRIM(brand_name)));

CREATE INDEX IF NOT EXISTS ix_brands_active_order
    ON public.brands (is_active, display_order, brand_name);

DROP TRIGGER IF EXISTS trg_brands_updated_at ON public.brands;
CREATE TRIGGER trg_brands_updated_at
BEFORE UPDATE ON public.brands
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

CREATE TABLE IF NOT EXISTS public.employee_brands
(
    employee_id     BIGINT      NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    brand_id        BIGINT      NOT NULL REFERENCES public.brands(id) ON DELETE RESTRICT,
    display_order   INTEGER     NOT NULL DEFAULT 1,
    assigned_at     TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    assigned_by     VARCHAR(100),
    PRIMARY KEY (employee_id, brand_id),
    CONSTRAINT ck_employee_brands_display_order CHECK (display_order >= 1)
);

-- Supports the reverse lookup: which active employees are responsible for a brand.
CREATE INDEX IF NOT EXISTS ix_employee_brands_brand_employee
    ON public.employee_brands (brand_id, employee_id);

COMMENT ON TABLE public.brands IS
    'Brand master data used for employee responsibility assignments.';
COMMENT ON TABLE public.employee_brands IS
    'Many-to-many relation between employees and the brands they are responsible for.';

COMMIT;
