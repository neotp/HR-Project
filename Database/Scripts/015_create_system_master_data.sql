BEGIN;

CREATE TABLE IF NOT EXISTS public.system_master_items
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    category_code   VARCHAR(60)  NOT NULL,
    item_code       VARCHAR(100) NOT NULL,
    name_th         VARCHAR(250) NOT NULL,
    name_en         VARCHAR(250),
    display_order   INTEGER      NOT NULL DEFAULT 0,
    is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_system_master_items_category_code UNIQUE (category_code, item_code),
    CONSTRAINT ck_system_master_items_display_order CHECK (display_order >= 0)
);

CREATE INDEX IF NOT EXISTS ix_system_master_items_category_active
    ON public.system_master_items(category_code, is_active, display_order);

DROP TRIGGER IF EXISTS trg_system_master_items_updated_at ON public.system_master_items;
CREATE TRIGGER trg_system_master_items_updated_at
BEFORE UPDATE ON public.system_master_items
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

-- Seed dropdown values already present in employee records.
INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'BUSINESS_UNIT', value, value, value
FROM (SELECT DISTINCT BTRIM(business_unit) value FROM public.employee_company_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'DEPARTMENT', value, value, value
FROM (SELECT DISTINCT BTRIM(department) value FROM public.employee_company_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'POSITION', value, value, value
FROM (SELECT DISTINCT BTRIM(position_name) value FROM public.employee_company_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'COMPANY', value, value, value
FROM (SELECT DISTINCT BTRIM(company_name) value FROM public.employee_company_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'EMPLOYMENT_TYPE', value, value, value
FROM (SELECT DISTINCT BTRIM(employment_type) value FROM public.employee_company_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'WORK_SCHEDULE', value, value, value
FROM (SELECT DISTINCT BTRIM(work_schedule) value FROM public.employee_company_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'WORK_LOCATION', value, value, value
FROM (SELECT DISTINCT BTRIM(work_location) value FROM public.employee_company_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'TITLE', value, value, value
FROM (SELECT DISTINCT BTRIM(title) value FROM public.employee_basic_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'RELIGION', value, value, value
FROM (SELECT DISTINCT BTRIM(religion) value FROM public.employee_personal_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'BLOOD_TYPE', value, value, value
FROM (SELECT DISTINCT BTRIM(blood_type) value FROM public.employee_personal_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items(category_code, item_code, name_th, name_en)
SELECT 'MARITAL_STATUS', value, value, value
FROM (SELECT DISTINCT BTRIM(marital_status) value FROM public.employee_family_info) source
WHERE value IS NOT NULL AND value <> ''
ON CONFLICT (category_code, item_code) DO NOTHING;

INSERT INTO public.system_master_items
    (category_code, item_code, name_th, name_en, display_order)
VALUES
    ('LEAVE_KIND', 'ADVANCE', 'ลาล่วงหน้า', 'Advance leave', 10),
    ('LEAVE_KIND', 'RETROACTIVE', 'ลาย้อนหลัง', 'Retroactive leave', 20)
ON CONFLICT (category_code, item_code) DO UPDATE SET
    name_th = EXCLUDED.name_th,
    name_en = EXCLUDED.name_en,
    display_order = EXCLUDED.display_order;

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order)
VALUES
    ('SYSTEM_MASTER_DATA', 'จัดการข้อมูลพื้นฐานระบบ', '/system/master-data', 'การจัดการระบบ', 100)
ON CONFLICT (page_key) DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

COMMIT;
