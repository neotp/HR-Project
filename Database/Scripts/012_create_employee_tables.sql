BEGIN;

CREATE TABLE IF NOT EXISTS public.employees
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_code   VARCHAR(50)  NOT NULL UNIQUE,
    is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
    source_system   VARCHAR(50),
    source_row      INTEGER,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Tab: ข้อมูลพื้นฐาน
CREATE TABLE IF NOT EXISTS public.employee_basic_info
(
    employee_id         BIGINT PRIMARY KEY REFERENCES public.employees(id) ON DELETE CASCADE,
    title               VARCHAR(30),
    first_name_th       VARCHAR(150),
    last_name_th        VARCHAR(150),
    full_name_th        VARCHAR(300),
    first_name_en       VARCHAR(150),
    last_name_en        VARCHAR(150),
    full_name_en        VARCHAR(300),
    nickname            VARCHAR(100),
    email_alias         VARCHAR(200),
    email_address       VARCHAR(320),
    personal_mobile     VARCHAR(50),
    home_phone          VARCHAR(50),
    profile_image_data  TEXT,
    updated_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_employee_basic_email_address
    ON public.employee_basic_info (UPPER(email_address))
    WHERE email_address IS NOT NULL AND BTRIM(email_address) <> '';

-- Tab: ข้อมูลส่วนบุคคล
CREATE TABLE IF NOT EXISTS public.employee_personal_info
(
    employee_id                     BIGINT PRIMARY KEY REFERENCES public.employees(id) ON DELETE CASCADE,
    national_id                     VARCHAR(30),
    birth_date                      DATE,
    gender                          VARCHAR(30),
    religion                        VARCHAR(100),
    blood_type                      VARCHAR(20),
    residence_province              VARCHAR(150),
    current_address                 TEXT,
    id_card_address                 TEXT,
    house_registration_address      TEXT,
    emergency_contact_name          VARCHAR(300),
    emergency_contact_phone         VARCHAR(50),
    emergency_contact_address       TEXT,
    updated_at                      TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Tab: ข้อมูลภายในบริษัท
CREATE TABLE IF NOT EXISTS public.employee_company_info
(
    employee_id                     BIGINT PRIMARY KEY REFERENCES public.employees(id) ON DELETE CASCADE,
    company_code                    VARCHAR(50),
    company_name                    VARCHAR(300),
    business_unit                   VARCHAR(200),
    division                        VARCHAR(200),
    department                      VARCHAR(200),
    section_name                    VARCHAR(200),
    position_name                   VARCHAR(200),
    job_code                        VARCHAR(80),
    supervisor_name                 VARCHAR(300),
    leave_approver_name             VARCHAR(300),
    functional_supervisor_name      VARCHAR(300),
    buddy_name                      VARCHAR(300),
    employment_type                 VARCHAR(100),
    work_schedule                   VARCHAR(200),
    work_location                   VARCHAR(300),
    internal_extension              VARCHAR(30),
    direct_phone                    VARCHAR(50),
    company_mobile                  VARCHAR(50),
    mac_address                     VARCHAR(100),
    branch_code                     VARCHAR(50),
    branch_name                     VARCHAR(200),
    responsibility_province         VARCHAR(150),
    checklist_type                  VARCHAR(100),
    products_responsible            TEXT,
    start_date                      DATE,
    appointment_date                DATE,
    provident_fund_start_date       DATE,
    work_experience_type            VARCHAR(100),
    has_company_parking             BOOLEAN,
    can_travel_upcountry            BOOLEAN,
    exclude_attendance_calculation  BOOLEAN NOT NULL DEFAULT FALSE,
    employee_status                 VARCHAR(50),
    updated_at                      TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

ALTER TABLE public.employee_company_info
    ADD COLUMN IF NOT EXISTS can_travel_upcountry BOOLEAN;

ALTER TABLE public.employee_company_info
    ADD COLUMN IF NOT EXISTS exclude_attendance_calculation BOOLEAN NOT NULL DEFAULT FALSE;

-- Tab: ประวัติการทำงาน (1:N)
CREATE TABLE IF NOT EXISTS public.employee_work_history
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id     BIGINT       NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    display_order   INTEGER      NOT NULL DEFAULT 1,
    period_text     VARCHAR(200),
    position_name   VARCHAR(200),
    company_name    VARCHAR(300),
    details         TEXT,
    can_travel_upcountry BOOLEAN,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_employee_work_history_order UNIQUE (employee_id, display_order)
);

-- Tab: ประวัติการศึกษา (1:N)
CREATE TABLE IF NOT EXISTS public.employee_education_history
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id         BIGINT       NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    display_order       INTEGER      NOT NULL DEFAULT 1,
    education_level     VARCHAR(150),
    institution_name    VARCHAR(300),
    major_name          VARCHAR(300),
    graduation_year     VARCHAR(20),
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_employee_education_history_order UNIQUE (employee_id, display_order)
);

-- Tab: ประวัติการอบรม & สัมมนา (1:N)
CREATE TABLE IF NOT EXISTS public.employee_training_history
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id         BIGINT       NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    display_order       INTEGER      NOT NULL DEFAULT 1,
    course_name         VARCHAR(300),
    training_period     VARCHAR(200),
    location_name       VARCHAR(300),
    organizer_name      VARCHAR(300),
    training_date       DATE,
    expense             NUMERIC(12,2) NOT NULL DEFAULT 0,
    certificate         VARCHAR(500),
    exam_fee            NUMERIC(12,2) NOT NULL DEFAULT 0,
    details             TEXT,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_employee_training_amounts CHECK (expense >= 0 AND exam_fee >= 0),
    CONSTRAINT ux_employee_training_history_order UNIQUE (employee_id, display_order)
);

-- Tab: ประวัติครอบครัว
CREATE TABLE IF NOT EXISTS public.employee_family_info
(
    employee_id                         BIGINT PRIMARY KEY REFERENCES public.employees(id) ON DELETE CASCADE,
    family_member_name                  VARCHAR(300),
    family_relationship                 VARCHAR(100),
    family_phone                        VARCHAR(50),
    family_occupation                   VARCHAR(200),
    marital_status                      VARCHAR(50),
    is_marriage_registered              BOOLEAN,
    spouse_title                        VARCHAR(30),
    spouse_name                         VARCHAR(300),
    marriage_date                       DATE,
    spouse_has_income                   BOOLEAN,
    spouse_national_id                  VARCHAR(30),
    spouse_passport_id                  VARCHAR(100),
    spouse_passport_name                VARCHAR(300),
    spouse_passport_file_name           VARCHAR(500),
    uneducated_child_count              INTEGER NOT NULL DEFAULT 0,
    studying_child_count                INTEGER NOT NULL DEFAULT 0,
    life_insurance_amount               NUMERIC(12,2) NOT NULL DEFAULT 0,
    parent_support_deduction_amount      NUMERIC(12,2) NOT NULL DEFAULT 0,
    spouse_parent_deduction_amount       NUMERIC(12,2) NOT NULL DEFAULT 0,
    current_address_map_url              TEXT,
    updated_at                          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_employee_family_counts CHECK (uneducated_child_count >= 0 AND studying_child_count >= 0),
    CONSTRAINT ck_employee_family_amounts CHECK
        (life_insurance_amount >= 0 AND parent_support_deduction_amount >= 0 AND spouse_parent_deduction_amount >= 0)
);

CREATE INDEX IF NOT EXISTS ix_employee_company_department
    ON public.employee_company_info (department, position_name);
CREATE INDEX IF NOT EXISTS ix_employee_basic_names
    ON public.employee_basic_info (full_name_th, full_name_en);
CREATE INDEX IF NOT EXISTS ix_employee_work_history_employee
    ON public.employee_work_history (employee_id, display_order);
CREATE INDEX IF NOT EXISTS ix_employee_education_history_employee
    ON public.employee_education_history (employee_id, display_order);
CREATE INDEX IF NOT EXISTS ix_employee_training_history_employee
    ON public.employee_training_history (employee_id, display_order);

DROP TRIGGER IF EXISTS trg_employees_updated_at ON public.employees;
CREATE TRIGGER trg_employees_updated_at BEFORE UPDATE ON public.employees
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();
DROP TRIGGER IF EXISTS trg_employee_basic_info_updated_at ON public.employee_basic_info;
CREATE TRIGGER trg_employee_basic_info_updated_at BEFORE UPDATE ON public.employee_basic_info
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();
DROP TRIGGER IF EXISTS trg_employee_personal_info_updated_at ON public.employee_personal_info;
CREATE TRIGGER trg_employee_personal_info_updated_at BEFORE UPDATE ON public.employee_personal_info
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();
DROP TRIGGER IF EXISTS trg_employee_company_info_updated_at ON public.employee_company_info;
CREATE TRIGGER trg_employee_company_info_updated_at BEFORE UPDATE ON public.employee_company_info
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();
DROP TRIGGER IF EXISTS trg_employee_work_history_updated_at ON public.employee_work_history;
CREATE TRIGGER trg_employee_work_history_updated_at BEFORE UPDATE ON public.employee_work_history
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();
DROP TRIGGER IF EXISTS trg_employee_education_history_updated_at ON public.employee_education_history;
CREATE TRIGGER trg_employee_education_history_updated_at BEFORE UPDATE ON public.employee_education_history
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();
DROP TRIGGER IF EXISTS trg_employee_training_history_updated_at ON public.employee_training_history;
CREATE TRIGGER trg_employee_training_history_updated_at BEFORE UPDATE ON public.employee_training_history
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();
DROP TRIGGER IF EXISTS trg_employee_family_info_updated_at ON public.employee_family_info;
CREATE TRIGGER trg_employee_family_info_updated_at BEFORE UPDATE ON public.employee_family_info
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

COMMIT;
