BEGIN;

UPDATE public.leave_types
SET name_th = CASE code
    WHEN 'PERSONAL' THEN 'ลากิจไม่รับเงินเดือน'
    WHEN 'UNPAID' THEN 'ลาคลอด/ลาบวช'
    ELSE name_th
END
WHERE code IN ('PERSONAL', 'UNPAID');

COMMIT;
