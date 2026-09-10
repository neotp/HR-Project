BEGIN;

ALTER TABLE public.employee_personal_info
    ADD COLUMN IF NOT EXISTS residence_district VARCHAR(150),
    ADD COLUMN IF NOT EXISTS residence_subdistrict VARCHAR(150),
    ADD COLUMN IF NOT EXISTS residence_postal_code VARCHAR(20);

-- Existing province values remain usable immediately in the new master-data dropdown.
INSERT INTO public.system_master_items
    (category_code, item_code, name_th, name_en, display_order, is_active)
SELECT 'PROVINCE', UPPER(BTRIM(value)), BTRIM(value), BTRIM(value),
       (ROW_NUMBER() OVER (ORDER BY BTRIM(value)))::INTEGER, TRUE
FROM
(
    SELECT DISTINCT residence_province AS value
    FROM public.employee_personal_info
    WHERE NULLIF(BTRIM(COALESCE(residence_province, '')), '') IS NOT NULL
) source
WHERE NOT EXISTS
(
    SELECT 1
    FROM public.system_master_items existing
    WHERE existing.category_code = 'PROVINCE'
      AND UPPER(BTRIM(existing.item_code)) = UPPER(BTRIM(source.value))
);

COMMIT;
