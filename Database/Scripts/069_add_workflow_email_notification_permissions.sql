BEGIN;

WITH action_seed(page_key, action_name, description) AS
(
    VALUES
        ('LEAVE_PENDING', 'รับอีเมลแจ้งเตือนใบลารออนุมัติ', 'รับอีเมลเมื่อมีใบลาใหม่ที่เกี่ยวข้องรออนุมัติ'),
        ('LEAVE_REVISIONS', 'รับอีเมลแจ้งเตือนคำขอแก้ไข/ยกเลิก', 'รับอีเมลเมื่อมีคำขอแก้ไขหรือยกเลิกเอกสารการลา'),
        ('LEAVE_REQUEST_QUOTA', 'รับอีเมลแจ้งเตือนคำขอวันลาเพิ่ม', 'รับอีเมลเมื่อมีคำขอเพิ่มโควต้าวันลาใหม่'),
        ('EMPLOYEE_EDIT_REQUESTS', 'รับอีเมลแจ้งเตือนคำขอแก้ไขข้อมูล', 'รับอีเมลเมื่อมีคำขอแก้ไขข้อมูลพนักงานใหม่'),
        ('PRE_EMPLOYEES', 'รับอีเมลแจ้งเตือน Pre-Employee', 'รับอีเมลเมื่อมีรายการ Pre-Employee ใหม่'),
        ('LOCAL_ACCOUNTS', 'รับอีเมลแจ้งเตือนลืมรหัสผ่าน', 'รับอีเมลเมื่อมีคำขอรีเซ็ตรหัสผ่าน Local ใหม่'),
        ('LOTUS_NOTES_EMPLOYEE_SYNC', 'รับอีเมลแจ้งเตือนส่ง Notes ไม่สำเร็จ', 'รับอีเมลเมื่อการส่งข้อมูลพนักงานไป Lotus Notes ไม่สำเร็จ')
)
INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT page.id, 'EMAIL_NOTIFICATION', seed.action_name, seed.description, 90
FROM action_seed seed
JOIN public.application_pages page ON page.page_key = seed.page_key
ON CONFLICT (application_page_id, action_key) DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

DO $$
DECLARE
    action_count integer;
BEGIN
    SELECT COUNT(*) INTO action_count
    FROM public.application_page_actions action
    JOIN public.application_pages page ON page.id = action.application_page_id
    WHERE action.action_key = 'EMAIL_NOTIFICATION'
      AND action.is_active = TRUE
      AND page.is_active = TRUE
      AND page.page_key IN
      (
          'LEAVE_PENDING', 'LEAVE_REVISIONS', 'LEAVE_REQUEST_QUOTA',
          'EMPLOYEE_EDIT_REQUESTS', 'PRE_EMPLOYEES', 'LOCAL_ACCOUNTS',
          'LOTUS_NOTES_EMPLOYEE_SYNC'
      );

    IF action_count <> 7 THEN
        RAISE EXCEPTION 'Expected 7 workflow email notification actions, found %', action_count;
    END IF;
END $$;

COMMIT;
