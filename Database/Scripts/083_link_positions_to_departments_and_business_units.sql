BEGIN;

-- Position uses Department as its direct parent. The Business Unit is inherited
-- from the Department, preventing a position from pointing to a mismatched BU.
UPDATE public.system_master_items
SET is_active = FALSE
WHERE category_code = 'POSITION'
  AND parent_item_id IS NULL;

INSERT INTO public.system_master_items
    (category_code, parent_item_id, item_code, name_th, name_en, is_active)
SELECT DISTINCT
    'POSITION',
    department.id,
    BTRIM(company.position_name),
    BTRIM(company.position_name),
    BTRIM(company.position_name),
    TRUE
FROM public.employee_company_info company
JOIN public.system_master_items business_unit
  ON business_unit.category_code = 'BUSINESS_UNIT'
 AND business_unit.item_code = BTRIM(company.business_unit)
JOIN public.system_master_items department
  ON department.category_code = 'DEPARTMENT'
 AND department.parent_item_id = business_unit.id
 AND department.item_code = BTRIM(company.department)
WHERE company.position_name IS NOT NULL
  AND BTRIM(company.position_name) <> ''
  AND company.business_unit IS NOT NULL
  AND BTRIM(company.business_unit) <> ''
  AND company.department IS NOT NULL
  AND BTRIM(company.department) <> ''
ON CONFLICT DO NOTHING;

WITH employee_positions AS
(
    SELECT DISTINCT
        department.id AS department_id,
        BTRIM(company.position_name) AS position_code
    FROM public.employee_company_info company
    JOIN public.system_master_items business_unit
      ON business_unit.category_code = 'BUSINESS_UNIT'
     AND business_unit.item_code = BTRIM(company.business_unit)
    JOIN public.system_master_items department
      ON department.category_code = 'DEPARTMENT'
     AND department.parent_item_id = business_unit.id
     AND department.item_code = BTRIM(company.department)
    WHERE COALESCE(BTRIM(company.position_name), '') <> ''
)
UPDATE public.system_master_items position
SET is_active = TRUE
FROM employee_positions source
WHERE position.category_code = 'POSITION'
  AND position.parent_item_id = source.department_id
  AND position.item_code = source.position_code;

COMMIT;
