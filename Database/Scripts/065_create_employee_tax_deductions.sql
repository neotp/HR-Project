BEGIN;

CREATE TABLE IF NOT EXISTS public.employee_tax_deduction_declarations
(
    id                              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id                     BIGINT NOT NULL REFERENCES public.employees(id),
    tax_year                        SMALLINT NOT NULL,
    status                          VARCHAR(20) NOT NULL DEFAULT 'DRAFT',
    marital_status                  VARCHAR(50),
    is_marriage_registered          BOOLEAN,
    spouse_name                     VARCHAR(300),
    spouse_has_income               BOOLEAN,
    spouse_national_id              VARCHAR(30),
    child_count                     INTEGER NOT NULL DEFAULT 0,
    child_born_from_2018_count      INTEGER NOT NULL DEFAULT 0,
    disabled_dependent_count        INTEGER NOT NULL DEFAULT 0,
    supports_father                 BOOLEAN NOT NULL DEFAULT FALSE,
    supports_mother                 BOOLEAN NOT NULL DEFAULT FALSE,
    supports_spouse_father          BOOLEAN NOT NULL DEFAULT FALSE,
    supports_spouse_mother          BOOLEAN NOT NULL DEFAULT FALSE,
    life_insurance_amount           NUMERIC(14,2) NOT NULL DEFAULT 0,
    health_insurance_amount         NUMERIC(14,2) NOT NULL DEFAULT 0,
    parent_health_insurance_amount  NUMERIC(14,2) NOT NULL DEFAULT 0,
    provident_fund_amount           NUMERIC(14,2) NOT NULL DEFAULT 0,
    retirement_fund_amount          NUMERIC(14,2) NOT NULL DEFAULT 0,
    social_security_amount          NUMERIC(14,2) NOT NULL DEFAULT 0,
    investment_deduction_amount     NUMERIC(14,2) NOT NULL DEFAULT 0,
    donation_amount                 NUMERIC(14,2) NOT NULL DEFAULT 0,
    other_deduction_description     TEXT,
    other_deduction_amount          NUMERIC(14,2) NOT NULL DEFAULT 0,
    note                            TEXT,
    confirmed_at                    TIMESTAMPTZ,
    confirmed_by                    VARCHAR(50),
    created_at                      TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by                      VARCHAR(50) NOT NULL,
    updated_at                      TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_by                      VARCHAR(50) NOT NULL,
    CONSTRAINT ux_employee_tax_deduction_year UNIQUE(employee_id, tax_year),
    CONSTRAINT ck_employee_tax_deduction_year CHECK(tax_year BETWEEN 2000 AND 2200),
    CONSTRAINT ck_employee_tax_deduction_status CHECK(status IN ('DRAFT', 'CONFIRMED')),
    CONSTRAINT ck_employee_tax_deduction_counts CHECK(
        child_count >= 0 AND child_born_from_2018_count >= 0 AND disabled_dependent_count >= 0),
    CONSTRAINT ck_employee_tax_deduction_amounts CHECK(
        life_insurance_amount >= 0 AND health_insurance_amount >= 0
        AND parent_health_insurance_amount >= 0 AND provident_fund_amount >= 0
        AND retirement_fund_amount >= 0 AND social_security_amount >= 0
        AND investment_deduction_amount >= 0 AND donation_amount >= 0
        AND other_deduction_amount >= 0)
);

CREATE TABLE IF NOT EXISTS public.employee_tax_deduction_history
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    declaration_id      BIGINT NOT NULL REFERENCES public.employee_tax_deduction_declarations(id),
    employee_id         BIGINT NOT NULL REFERENCES public.employees(id),
    tax_year            SMALLINT NOT NULL,
    action              VARCHAR(30) NOT NULL,
    status              VARCHAR(20) NOT NULL,
    data_snapshot       JSONB NOT NULL,
    changed_by          VARCHAR(50) NOT NULL,
    changed_by_name     VARCHAR(300),
    changed_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_employee_tax_deduction_report
    ON public.employee_tax_deduction_declarations(tax_year, status, employee_id);
CREATE INDEX IF NOT EXISTS ix_employee_tax_deduction_history
    ON public.employee_tax_deduction_history(declaration_id, changed_at DESC);

-- ย้ายค่าที่เคยเก็บรวมกับข้อมูลครอบครัวมาเป็นแบบร่างของปีที่รัน migration เพียงครั้งเดียว
INSERT INTO public.employee_tax_deduction_declarations
    (employee_id,tax_year,status,marital_status,is_marriage_registered,spouse_name,
     spouse_has_income,spouse_national_id,child_count,life_insurance_amount,
     created_by,updated_by)
SELECT family.employee_id,EXTRACT(YEAR FROM CURRENT_DATE)::SMALLINT,'DRAFT',family.marital_status,
       family.is_marriage_registered,family.spouse_name,family.spouse_has_income,
       family.spouse_national_id,
       COALESCE(family.uneducated_child_count,0)+COALESCE(family.studying_child_count,0),
       COALESCE(family.life_insurance_amount,0),'MIGRATION','MIGRATION'
FROM public.employee_family_info family
WHERE NULLIF(BTRIM(COALESCE(family.marital_status,'')),'') IS NOT NULL
   OR NULLIF(BTRIM(COALESCE(family.spouse_name,'')),'') IS NOT NULL
   OR COALESCE(family.uneducated_child_count,0)+COALESCE(family.studying_child_count,0)>0
   OR COALESCE(family.life_insurance_amount,0)>0
ON CONFLICT(employee_id,tax_year) DO NOTHING;

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order)
VALUES
    ('EMPLOYEE_TAX_DEDUCTION_REPORT', 'รายงานค่าลดหย่อนภาษีพนักงาน',
     '/employees/tax-deductions', 'พนักงาน', 84)
ON CONFLICT (page_key) DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

INSERT INTO public.application_page_actions(application_page_id, action_key, action_name, display_order)
SELECT id, 'EXPORT', 'Export ข้อมูล', 10
FROM public.application_pages WHERE page_key='EMPLOYEE_TAX_DEDUCTION_REPORT'
ON CONFLICT (application_page_id, action_key) DO UPDATE SET
    action_name=EXCLUDED.action_name, display_order=EXCLUDED.display_order, is_active=TRUE;

-- ไม่กำหนดสิทธิ์เริ่มต้น หน้ารายงานจะไม่แสดงจนกว่าจะกำหนดในหน้าจัดการสิทธิ์
COMMIT;
