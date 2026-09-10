BEGIN;

-- Source: Thailand Administrative Divisions Dataset, CC BY 4.0
-- https://github.com/open-admin-data/thailand-administrative-divisions
-- Dataset snapshot is downloaded by the migration command and supplied as @address_json.

DELETE FROM public.system_master_items WHERE category_code = 'POSTAL_CODE';
DELETE FROM public.system_master_items WHERE category_code = 'SUBDISTRICT';
DELETE FROM public.system_master_items WHERE category_code = 'DISTRICT';
DELETE FROM public.system_master_items WHERE category_code = 'PROVINCE';

CREATE TEMP TABLE thai_address_source AS
SELECT element, ordinal::INTEGER AS display_order
FROM jsonb_array_elements(CAST(@address_json AS jsonb)) WITH ORDINALITY AS source(element, ordinal);

INSERT INTO public.system_master_items
    (category_code, item_code, name_th, name_en, display_order, is_active)
SELECT 'PROVINCE', province_code, province_name_th, province_name_en,
       ROW_NUMBER() OVER (ORDER BY province_code), TRUE
FROM
(
    SELECT DISTINCT
        element->'ancestors'->0->>'id' AS province_code,
        element->'ancestors'->0->'name'->>'local' AS province_name_th,
        element->'ancestors'->0->'name'->>'en' AS province_name_en
    FROM thai_address_source
) province
ORDER BY province_code;

INSERT INTO public.system_master_items
    (category_code, parent_item_id, item_code, name_th, name_en, display_order, is_active)
SELECT 'DISTRICT', province.id, district_code, district_name_th, district_name_en,
       ROW_NUMBER() OVER (PARTITION BY province.id ORDER BY district_code), TRUE
FROM
(
    SELECT DISTINCT
        element->'ancestors'->0->>'id' AS province_code,
        element->'parent'->>'id' AS district_code,
        element->'parent'->'name'->>'local' AS district_name_th,
        element->'parent'->'name'->>'en' AS district_name_en
    FROM thai_address_source
) district
JOIN public.system_master_items province
  ON province.category_code = 'PROVINCE'
 AND province.item_code = district.province_code
ORDER BY district_code;

INSERT INTO public.system_master_items
    (category_code, parent_item_id, item_code, name_th, name_en, display_order, is_active)
SELECT 'SUBDISTRICT', district.id, source.element->>'id',
       source.element->'name'->>'local', source.element->'name'->>'en',
       ROW_NUMBER() OVER (PARTITION BY district.id ORDER BY source.element->>'id'), TRUE
FROM thai_address_source source
JOIN public.system_master_items district
  ON district.category_code = 'DISTRICT'
 AND district.item_code = source.element->'parent'->>'id'
ORDER BY source.element->>'id';

INSERT INTO public.system_master_items
    (category_code, parent_item_id, item_code, name_th, name_en, display_order, is_active)
SELECT 'POSTAL_CODE', subdistrict.id, postal_code.value,
       postal_code.value, postal_code.value,
       ROW_NUMBER() OVER (PARTITION BY subdistrict.id ORDER BY postal_code.value), TRUE
FROM thai_address_source source
JOIN public.system_master_items subdistrict
  ON subdistrict.category_code = 'SUBDISTRICT'
 AND subdistrict.item_code = source.element->>'id'
CROSS JOIN LATERAL jsonb_array_elements_text(source.element->'zip_codes') postal_code(value)
ORDER BY source.element->>'id', postal_code.value;

COMMIT;
