BEGIN;

CREATE TABLE IF NOT EXISTS public.application_page_actions
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    application_page_id BIGINT       NOT NULL REFERENCES public.application_pages(id) ON DELETE CASCADE,
    action_key          VARCHAR(80)  NOT NULL,
    action_name         VARCHAR(200) NOT NULL,
    description         TEXT,
    display_order       INTEGER      NOT NULL DEFAULT 0,
    is_active           BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_application_page_actions UNIQUE(application_page_id, action_key)
);

CREATE TABLE IF NOT EXISTS public.employee_page_action_permissions
(
    id                          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id                 VARCHAR(50)  NOT NULL,
    application_page_action_id  BIGINT       NOT NULL REFERENCES public.application_page_actions(id) ON DELETE CASCADE,
    can_execute                 BOOLEAN      NOT NULL DEFAULT FALSE,
    updated_by                  VARCHAR(50)  NOT NULL,
    updated_by_name             VARCHAR(200) NOT NULL,
    created_at                  TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                  TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_employee_page_action_permissions UNIQUE(employee_id, application_page_action_id)
);

CREATE INDEX IF NOT EXISTS ix_employee_page_action_permissions_employee
    ON public.employee_page_action_permissions(employee_id);

DROP TRIGGER IF EXISTS trg_application_page_actions_updated_at ON public.application_page_actions;
CREATE TRIGGER trg_application_page_actions_updated_at
BEFORE UPDATE ON public.application_page_actions
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

DROP TRIGGER IF EXISTS trg_employee_page_action_permissions_updated_at ON public.employee_page_action_permissions;
CREATE TRIGGER trg_employee_page_action_permissions_updated_at
BEFORE UPDATE ON public.employee_page_action_permissions
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

WITH action_seed(page_key, action_key, action_name, description, display_order) AS
(
    VALUES
    ('LEAVE_DOCUMENTS', 'VIEW_ALL', 'ดูเอกสารของทุกคน', 'ดูเอกสารลาที่ไม่ได้เป็นของตนเอง', 10),
    ('LEAVE_DOCUMENTS', 'VIEW_EDIT_DETAILS', 'ดูรายละเอียดการแก้ไข', 'ดูข้อมูลเดิมและข้อมูลที่ขอแก้ไข', 20),
    ('LEAVE_DOCUMENTS', 'CREATE', 'สร้างเอกสาร', 'สร้างเอกสารการลา', 30),
    ('LEAVE_DOCUMENTS', 'EDIT', 'แก้ไขเอกสาร', 'แก้ไขเอกสารที่อยู่ระหว่างรออนุมัติ', 40),
    ('LEAVE_DOCUMENTS', 'CANCEL', 'ยกเลิกเอกสาร', 'ยกเลิกเอกสารการลา', 50),
    ('LEAVE_DOCUMENTS', 'REQUEST_EDIT', 'ขอแก้ไขเอกสาร', 'สร้างคำขอแก้ไขเอกสารที่อนุมัติแล้ว', 60),
    ('LEAVE_DOCUMENTS', 'VIEW_HISTORY', 'ดูประวัติเอกสาร', 'ดูประวัติการดำเนินการของเอกสาร', 70),
    ('LEAVE_ALL_DOCUMENTS', 'VIEW_ALL', 'ดูเอกสารทั้งหมด', 'ดูเอกสารการลาของพนักงานทุกคนโดยไม่รวมไฟล์แนบ', 10),
    ('LEAVE_PENDING', 'VIEW_ALL', 'ดูเอกสารรออนุมัติทั้งหมด', 'ดูเอกสารที่ตนไม่ได้เป็นผู้อนุมัติ', 10),
    ('LEAVE_PENDING', 'APPROVE', 'อนุมัติเอกสาร', 'อนุมัติเอกสารแทนผู้อนุมัติตามสายงาน', 20),
    ('LEAVE_PENDING', 'REJECT', 'ไม่อนุมัติเอกสาร', 'ไม่อนุมัติเอกสารแทนผู้อนุมัติตามสายงาน', 30),
    ('LEAVE_REVISIONS', 'VIEW_ALL', 'ดูคำขอแก้ไขทั้งหมด', 'ดูคำขอแก้ไขที่ตนไม่ได้เป็นผู้อนุมัติ', 10),
    ('LEAVE_REVISIONS', 'APPROVE_EDIT', 'อนุมัติคำขอแก้ไข', 'อนุมัติคำขอแก้ไขเอกสารการลา', 20),
    ('LEAVE_REVISIONS', 'REJECT_EDIT', 'ไม่อนุมัติคำขอแก้ไข', 'ไม่อนุมัติคำขอแก้ไขเอกสารการลา', 30),
    ('LEAVE_TEAM', 'CREATE_FOR_OTHERS', 'แจ้งข้อมูลให้พนักงานทุกคน', 'ส่งอีเมลแจ้งข้อมูลให้พนักงานนอกเหนือจาก Boss หรือ Reporting To ของตนเอง', 10),
    ('LEAVE_REQUEST_QUOTA', 'VIEW_ALL', 'ดูคำขอเพิ่มโควต้าทั้งหมด', 'ดูคำขอของพนักงานทุกคน', 10),
    ('LEAVE_REQUEST_QUOTA', 'CREATE_FOR_OTHERS', 'ขอโควต้าแทนผู้อื่น', 'สร้างคำขอเพิ่มโควต้าให้พนักงานอื่น', 20),
    ('LEAVE_REQUEST_QUOTA', 'APPROVE', 'อนุมัติคำขอเพิ่มโควต้า', 'อนุมัติและเพิ่มจำนวนชั่วโมงเข้าโควต้าพนักงาน', 30),
    ('LEAVE_REQUEST_QUOTA', 'REJECT', 'ไม่อนุมัติคำขอเพิ่มโควต้า', 'ไม่อนุมัติคำขอเพิ่มโควต้าพนักงาน', 40),
    ('LEAVE_MANAGE_QUOTA', 'CREATE', 'กำหนดโควต้า', 'สร้างโควต้าพนักงาน', 10),
    ('LEAVE_MANAGE_QUOTA', 'EDIT', 'แก้ไขโควต้า', 'แก้ไขจำนวนโควต้า', 20),
    ('LEAVE_MANAGE_QUOTA', 'DELETE', 'ลบโควต้า', 'ลบโควต้าของพนักงาน', 30),
    ('EMPLOYEES', 'VIEW_PERSONAL', 'ดูข้อมูลส่วนบุคคล', 'ดูข้อมูลส่วนบุคคลของพนักงาน', 10),
    ('EMPLOYEES', 'VIEW_COMPANY', 'ดูข้อมูลภายในบริษัท', 'ดูข้อมูลภายในบริษัทของพนักงาน', 20),
      ('EMPLOYEES', 'VIEW_PERSONAL_DOCUMENTS', 'ดูและเพิ่มเอกสารส่วนตัว', 'ดู เพิ่ม และเปิดพรีวิวเอกสารส่วนตัวของพนักงาน', 25),
    ('EMPLOYEES', 'CREATE', 'เพิ่มพนักงาน', 'สร้างข้อมูลพนักงานใหม่', 30),
    ('EMPLOYEE_EDIT_REQUESTS', 'VIEW_ALL', 'ดูคำขอแก้ไขทั้งหมด', 'ดูคำขอแก้ไขข้อมูลพนักงานทุกคน', 10),
    ('EMPLOYEE_EDIT_REQUESTS', 'APPROVE', 'อนุมัติคำขอแก้ไข', 'อนุมัติการแก้ไขข้อมูลพนักงาน', 20),
    ('EMPLOYEE_EDIT_REQUESTS', 'REJECT', 'ไม่อนุมัติคำขอแก้ไข', 'ไม่อนุมัติการแก้ไขข้อมูลพนักงาน', 30),
    ('ATTENDANCE', 'VIEW_ALL', 'ดูข้อมูลการทำงานทุกคน', 'ดูปฏิทินการทำงานของพนักงานอื่น', 10),
    ('WORK_CALENDAR', 'MANAGE', 'จัดการปฏิทินการทำงาน', 'เพิ่ม แก้ไข และลบวันหยุดหรือวันทำงาน', 10),
    ('PERMISSIONS', 'MANAGE_PAGE_ACCESS', 'จัดการสิทธิ์เข้าถึงหน้า', 'กำหนดสิทธิ์เข้าถึงหน้าระบบ', 10),
    ('PERMISSIONS', 'MANAGE_PAGE_ACTIONS', 'จัดการสิทธิ์เพิ่มเติม', 'กำหนดสิทธิ์การทำงานภายในหน้า', 20),
    ('SYSTEM_MASTER_DATA', 'CREATE', 'เพิ่มข้อมูลพื้นฐาน', 'เพิ่มตัวเลือก Dropdown', 10),
    ('SYSTEM_MASTER_DATA', 'EDIT', 'แก้ไขข้อมูลพื้นฐาน', 'แก้ไขตัวเลือกและค่าเริ่มต้น', 20),
    ('SYSTEM_MASTER_DATA', 'DEACTIVATE', 'ปิดใช้งานข้อมูลพื้นฐาน', 'เปิดหรือปิดตัวเลือก Dropdown', 30)
)
INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT page.id, seed.action_key, seed.action_name, seed.description, seed.display_order
FROM action_seed seed
JOIN public.application_pages page ON page.page_key = seed.page_key
ON CONFLICT (application_page_id, action_key) DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

UPDATE public.application_page_actions action
SET is_active = FALSE
FROM public.application_pages page
WHERE action.application_page_id = page.id
  AND page.page_key = 'EMPLOYEES'
  AND action.action_key = 'EDIT';

COMMIT;
