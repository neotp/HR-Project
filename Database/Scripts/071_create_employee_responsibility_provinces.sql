BEGIN;

CREATE TABLE IF NOT EXISTS public.employee_responsibility_provinces (
    employee_id BIGINT NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    province_id BIGINT NOT NULL REFERENCES public.system_master_items(id),
    display_order INTEGER NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (employee_id, province_id)
);

CREATE INDEX IF NOT EXISTS ix_employee_responsibility_provinces_province
    ON public.employee_responsibility_provinces(province_id);

CREATE TABLE IF NOT EXISTS public.pre_employee_responsibility_provinces (
    pre_employee_id BIGINT NOT NULL REFERENCES public.pre_employees(id) ON DELETE CASCADE,
    province_id BIGINT NOT NULL REFERENCES public.system_master_items(id),
    display_order INTEGER NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (pre_employee_id, province_id)
);

CREATE INDEX IF NOT EXISTS ix_pre_employee_responsibility_provinces_province
    ON public.pre_employee_responsibility_provinces(province_id);

WITH source_values AS (
    SELECT c.employee_id,
           trim(value) AS province_name,
           value_order::integer AS display_order
    FROM public.employee_company_info c
    CROSS JOIN LATERAL unnest(
        regexp_split_to_array(COALESCE(c.responsibility_province, ''), '\\s*[,|;]\\s*')
    ) WITH ORDINALITY AS split(value, value_order)
    WHERE trim(value) <> ''
), matched AS (
    SELECT DISTINCT ON (source.employee_id, master.id)
           source.employee_id, master.id AS province_id, source.display_order
    FROM source_values source
    JOIN public.system_master_items master
      ON master.category_code = 'PROVINCE'
     AND (lower(trim(master.name_th)) = lower(source.province_name)
       OR lower(trim(COALESCE(master.name_en, ''))) = lower(source.province_name))
    ORDER BY source.employee_id, master.id, source.display_order
)
INSERT INTO public.employee_responsibility_provinces(employee_id, province_id, display_order)
SELECT employee_id, province_id, display_order FROM matched
ON CONFLICT (employee_id, province_id) DO NOTHING;

WITH source_values AS (
    SELECT p.id AS pre_employee_id,
           trim(value) AS province_name,
           value_order::integer AS display_order
    FROM public.pre_employees p
    CROSS JOIN LATERAL unnest(
        regexp_split_to_array(COALESCE(p.employee_data ->> 'responsibilityProvince', ''), '\\s*[,|;]\\s*')
    ) WITH ORDINALITY AS split(value, value_order)
    WHERE trim(value) <> ''
), matched AS (
    SELECT DISTINCT ON (source.pre_employee_id, master.id)
           source.pre_employee_id, master.id AS province_id, source.display_order
    FROM source_values source
    JOIN public.system_master_items master
      ON master.category_code = 'PROVINCE'
     AND (lower(trim(master.name_th)) = lower(source.province_name)
       OR lower(trim(COALESCE(master.name_en, ''))) = lower(source.province_name))
    ORDER BY source.pre_employee_id, master.id, source.display_order
)
INSERT INTO public.pre_employee_responsibility_provinces(pre_employee_id, province_id, display_order)
SELECT pre_employee_id, province_id, display_order FROM matched
ON CONFLICT (pre_employee_id, province_id) DO NOTHING;

COMMIT;
