BEGIN;

ALTER TABLE public.pre_employees
    ADD COLUMN IF NOT EXISTS employee_data JSONB;

UPDATE public.pre_employees
SET employee_data = jsonb_build_object(
    'employeeCode', COALESCE(employee_code, ''),
    'title', COALESCE(title, ''),
    'firstName', COALESCE(first_name_th, ''),
    'lastName', COALESCE(last_name_th, ''),
    'thaiFullName', BTRIM(CONCAT_WS(' ', first_name_th, last_name_th)),
    'englishFullName', COALESCE(full_name_en, ''),
    'nickname', COALESCE(nickname, ''),
    'email', COALESCE(email_address, ''),
    'lotusNotesEmail', COALESCE(email_alias, ''),
    'personalMobile', COALESCE(personal_mobile, ''),
    'company', COALESCE(company_name, ''),
    'businessUnit', COALESCE(business_unit, ''),
    'department', COALESCE(department, ''),
    'position', COALESCE(position_name, ''),
    'startDate', start_date,
    'supervisorName', COALESCE(supervisor_name, ''),
    'leaveApproverName', COALESCE(leave_approver_name, ''),
    'employmentType', COALESCE(employment_type, ''),
    'workLocation', COALESCE(work_location, ''),
    'workHistory', '[]'::jsonb,
    'educationHistory', '[]'::jsonb,
    'trainingHistory', '[]'::jsonb
)
WHERE employee_data IS NULL;

ALTER TABLE public.pre_employees
    ALTER COLUMN employee_data SET DEFAULT '{}'::jsonb;

COMMIT;
