BEGIN;

ALTER TABLE public.system_master_items
    ADD COLUMN IF NOT EXISTS parent_item_id BIGINT;

DO $$
BEGIN
    IF NOT EXISTS
    (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_system_master_items_parent'
          AND conrelid = 'public.system_master_items'::regclass
    ) THEN
        ALTER TABLE public.system_master_items
            ADD CONSTRAINT fk_system_master_items_parent
            FOREIGN KEY (parent_item_id)
            REFERENCES public.system_master_items(id);
    END IF;
END $$;

ALTER TABLE public.system_master_items
    DROP CONSTRAINT IF EXISTS ux_system_master_items_category_code;

CREATE UNIQUE INDEX IF NOT EXISTS ux_system_master_items_category_parent_code
    ON public.system_master_items
        (category_code, COALESCE(parent_item_id, 0), item_code);

-- Rebuild department master records from the employee BU/department pairs.
DELETE FROM public.system_master_items
WHERE category_code = 'DEPARTMENT';

INSERT INTO public.system_master_items
    (category_code, parent_item_id, item_code, name_th, name_en)
SELECT DISTINCT
    'DEPARTMENT',
    business_unit.id,
    BTRIM(company.department),
    BTRIM(company.department),
    BTRIM(company.department)
FROM public.employee_company_info company
JOIN public.system_master_items business_unit
  ON business_unit.category_code = 'BUSINESS_UNIT'
 AND business_unit.item_code = BTRIM(company.business_unit)
WHERE company.department IS NOT NULL
  AND BTRIM(company.department) <> ''
  AND company.business_unit IS NOT NULL
  AND BTRIM(company.business_unit) <> ''
ON CONFLICT DO NOTHING;

COMMIT;
