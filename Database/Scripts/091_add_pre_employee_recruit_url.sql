BEGIN;

ALTER TABLE public.pre_employees
    ADD COLUMN IF NOT EXISTS recruit_url VARCHAR(2048);

WITH source_urls AS
(
    SELECT pre_employee.id,
           NULLIF(BTRIM(source_field.value), '') AS recruit_url
    FROM public.pre_employees pre_employee
    CROSS JOIN LATERAL jsonb_each_text(COALESCE(pre_employee.source_payload, '{}'::jsonb)) source_field
    WHERE LOWER(BTRIM(source_field.key)) = 'url'
      AND source_field.value ~* '^https?://'
)
UPDATE public.pre_employees pre_employee
SET recruit_url = source_urls.recruit_url
FROM source_urls
WHERE pre_employee.id = source_urls.id
  AND NULLIF(BTRIM(pre_employee.recruit_url), '') IS NULL;

ALTER TABLE public.pre_employees
    DROP CONSTRAINT IF EXISTS ck_pre_employees_recruit_url;

ALTER TABLE public.pre_employees
    ADD CONSTRAINT ck_pre_employees_recruit_url
        CHECK (recruit_url IS NULL OR BTRIM(recruit_url) = '' OR recruit_url ~* '^https?://');

COMMIT;
