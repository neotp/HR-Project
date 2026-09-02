BEGIN;

CREATE TABLE IF NOT EXISTS public.leave_quota_movements
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_quota_id      BIGINT REFERENCES public.leave_quotas(id) ON DELETE SET NULL,
    employee_id         VARCHAR(50)  NOT NULL,
    leave_type_id       BIGINT       NOT NULL REFERENCES public.leave_types(id),
    quota_year          SMALLINT     NOT NULL,
    movement_type       VARCHAR(40)  NOT NULL,
    source_type         VARCHAR(40)  NOT NULL,
    source_id           BIGINT,
    reference_no        VARCHAR(50),
    change_hours        NUMERIC(10,2) NOT NULL,
    notes               TEXT,
    action_by           VARCHAR(50)  NOT NULL,
    action_by_name      VARCHAR(200) NOT NULL,
    occurred_at         TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_at          TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_leave_quota_movements_year CHECK (quota_year BETWEEN 2000 AND 2200),
    CONSTRAINT ck_leave_quota_movements_non_zero CHECK (change_hours <> 0)
);

CREATE INDEX IF NOT EXISTS ix_leave_quota_movements_employee_year_time
    ON public.leave_quota_movements(employee_id, quota_year, occurred_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_leave_quota_movements_type_year_time
    ON public.leave_quota_movements(leave_type_id, quota_year, occurred_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS ix_leave_quota_movements_reference
    ON public.leave_quota_movements(reference_no)
    WHERE reference_no IS NOT NULL;

-- Establish the current ledger position for existing data. Historical rejected
-- or cancelled documents are intentionally not fabricated; future transitions
-- are recorded by the triggers below.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.leave_quota_movements LIMIT 1) THEN
        INSERT INTO public.leave_quota_movements
            (leave_quota_id, employee_id, leave_type_id, quota_year,
             movement_type, source_type, source_id, reference_no,
             change_hours, notes, action_by, action_by_name, occurred_at)
        SELECT q.id, q.employee_id, q.leave_type_id, q.quota_year,
               'OPENING_QUOTA', 'LEAVE_QUOTA', q.id, NULL,
               q.quota_hours, 'ยอดโควต้าเริ่มต้น ณ วันที่เริ่มใช้ Movement',
               COALESCE(NULLIF(q.updated_by, ''), 'SYSTEM'),
               COALESCE(NULLIF(q.updated_by_name, ''), 'ระบบอัตโนมัติ'),
               q.created_at
        FROM public.leave_quotas q
        WHERE q.quota_hours <> 0;

        INSERT INTO public.leave_quota_movements
            (leave_quota_id, employee_id, leave_type_id, quota_year,
             movement_type, source_type, source_id, reference_no,
             change_hours, notes, action_by, action_by_name, occurred_at)
        SELECT q.id, d.creator_employee_id, d.leave_type_id,
               EXTRACT(YEAR FROM d.leave_date)::SMALLINT,
               'LEAVE_RESERVED', 'LEAVE_DOCUMENT', d.id, d.document_no,
               -d.leave_hours, 'ยอดใช้จากเอกสารที่ยังมีผลอยู่ ณ วันที่เริ่มใช้ Movement',
               d.creator_employee_id, d.creator_name, d.created_at
        FROM public.leave_documents d
        LEFT JOIN public.leave_quotas q
          ON q.employee_id = d.creator_employee_id
         AND q.leave_type_id = d.leave_type_id
         AND q.quota_year = EXTRACT(YEAR FROM d.leave_date)::INT
        WHERE d.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED')
          AND d.leave_hours <> 0;
    END IF;
END $$;

CREATE OR REPLACE FUNCTION public.track_leave_quota_master_movement()
RETURNS TRIGGER AS $$
DECLARE
    delta NUMERIC(10,2);
    used_hours NUMERIC(10,2);
BEGIN
    IF TG_OP = 'INSERT' THEN
        delta := NEW.quota_hours;
        IF delta <> 0 THEN
            INSERT INTO public.leave_quota_movements
                (leave_quota_id, employee_id, leave_type_id, quota_year,
                 movement_type, source_type, source_id, change_hours, notes,
                 action_by, action_by_name)
            VALUES
                (NEW.id, NEW.employee_id, NEW.leave_type_id, NEW.quota_year,
                 'QUOTA_CREATED', 'LEAVE_QUOTA', NEW.id, delta,
                 COALESCE(NEW.notes, 'กำหนดโควต้าวันลา'),
                 NEW.created_by, NEW.created_by_name);
        END IF;
        RETURN NEW;
    ELSIF TG_OP = 'UPDATE' THEN
        delta := NEW.quota_hours - OLD.quota_hours;
        IF delta <> 0 THEN
            INSERT INTO public.leave_quota_movements
                (leave_quota_id, employee_id, leave_type_id, quota_year,
                 movement_type, source_type, source_id, change_hours, notes,
                 action_by, action_by_name)
            VALUES
                (NEW.id, NEW.employee_id, NEW.leave_type_id, NEW.quota_year,
                 'QUOTA_ADJUSTED', 'LEAVE_QUOTA', NEW.id, delta,
                 COALESCE(NEW.notes, 'ปรับจำนวนโควต้าวันลา'),
                 NEW.updated_by, NEW.updated_by_name);
        END IF;
        RETURN NEW;
    ELSE
        SELECT COALESCE(SUM(d.leave_hours), 0)
          INTO used_hours
          FROM public.leave_documents d
         WHERE d.creator_employee_id = OLD.employee_id
           AND d.leave_type_id = OLD.leave_type_id
           AND EXTRACT(YEAR FROM d.leave_date)::INT = OLD.quota_year
           AND d.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED');

        -- Remove only the available balance. Leave reservations remain in the
        -- ledger as historical usage, while a deleted quota closes at zero.
        delta := OLD.quota_hours - used_hours;
        IF delta <> 0 THEN
            INSERT INTO public.leave_quota_movements
                (leave_quota_id, employee_id, leave_type_id, quota_year,
                 movement_type, source_type, source_id, change_hours, notes,
                 action_by, action_by_name)
            VALUES
                (NULL, OLD.employee_id, OLD.leave_type_id, OLD.quota_year,
                 'QUOTA_REMOVED', 'LEAVE_QUOTA', OLD.id, -delta,
                 'ลบโควต้าวันลา', COALESCE(NULLIF(OLD.updated_by, ''), 'SYSTEM'),
                 COALESCE(NULLIF(OLD.updated_by_name, ''), 'ระบบอัตโนมัติ'));
        END IF;
        RETURN OLD;
    END IF;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_leave_quota_master_movement ON public.leave_quotas;
CREATE TRIGGER trg_leave_quota_master_movement
AFTER INSERT OR UPDATE OF quota_hours OR DELETE ON public.leave_quotas
FOR EACH ROW EXECUTE FUNCTION public.track_leave_quota_master_movement();

CREATE OR REPLACE FUNCTION public.track_leave_document_quota_movement()
RETURNS TRIGGER AS $$
DECLARE
    old_active BOOLEAN := FALSE;
    new_active BOOLEAN := FALSE;
    old_year SMALLINT;
    new_year SMALLINT;
    old_quota_id BIGINT;
    new_quota_id BIGINT;
    delta NUMERIC(10,2);
BEGIN
    IF TG_OP <> 'INSERT' THEN
        old_active := OLD.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED');
        old_year := EXTRACT(YEAR FROM OLD.leave_date)::SMALLINT;
        SELECT id INTO old_quota_id FROM public.leave_quotas
         WHERE employee_id = OLD.creator_employee_id
           AND leave_type_id = OLD.leave_type_id AND quota_year = old_year;
    END IF;

    new_active := NEW.status IN ('PENDING_APPROVAL', 'APPROVED', 'EDIT_REQUESTED');
    new_year := EXTRACT(YEAR FROM NEW.leave_date)::SMALLINT;
    SELECT id INTO new_quota_id FROM public.leave_quotas
     WHERE employee_id = NEW.creator_employee_id
       AND leave_type_id = NEW.leave_type_id AND quota_year = new_year;

    IF TG_OP = 'INSERT' THEN
        IF new_active AND NEW.leave_hours <> 0 THEN
            INSERT INTO public.leave_quota_movements
                (leave_quota_id, employee_id, leave_type_id, quota_year,
                 movement_type, source_type, source_id, reference_no,
                 change_hours, notes, action_by, action_by_name, occurred_at)
            VALUES
                (new_quota_id, NEW.creator_employee_id, NEW.leave_type_id, new_year,
                 'LEAVE_RESERVED', 'LEAVE_DOCUMENT', NEW.id, NEW.document_no,
                 -NEW.leave_hours, 'ตัดโควต้าเมื่อสร้างเอกสารลา',
                 NEW.creator_employee_id, NEW.creator_name, NEW.created_at);
        END IF;
        RETURN NEW;
    END IF;

    -- The document number is assigned after insert. Keep the original movement
    -- linked to the final number without generating an additional quantity row.
    IF OLD.document_no IS DISTINCT FROM NEW.document_no THEN
        UPDATE public.leave_quota_movements
        SET reference_no = NEW.document_no
        WHERE source_type = 'LEAVE_DOCUMENT' AND source_id = NEW.id;
    END IF;

    IF old_active AND new_active
       AND OLD.creator_employee_id = NEW.creator_employee_id
       AND OLD.leave_type_id = NEW.leave_type_id
       AND old_year = new_year THEN
        delta := OLD.leave_hours - NEW.leave_hours;
        IF delta <> 0 THEN
            INSERT INTO public.leave_quota_movements
                (leave_quota_id, employee_id, leave_type_id, quota_year,
                 movement_type, source_type, source_id, reference_no,
                 change_hours, notes, action_by, action_by_name)
            VALUES
                (new_quota_id, NEW.creator_employee_id, NEW.leave_type_id, new_year,
                 'LEAVE_ADJUSTED', 'LEAVE_DOCUMENT', NEW.id, NEW.document_no,
                 delta, 'ปรับจำนวนชั่วโมงของเอกสารลา',
                 'SYSTEM', 'ระบบอัตโนมัติ');
        END IF;
    ELSE
        IF old_active AND OLD.leave_hours <> 0 THEN
            INSERT INTO public.leave_quota_movements
                (leave_quota_id, employee_id, leave_type_id, quota_year,
                 movement_type, source_type, source_id, reference_no,
                 change_hours, notes, action_by, action_by_name)
            VALUES
                (old_quota_id, OLD.creator_employee_id, OLD.leave_type_id, old_year,
                 'LEAVE_RETURNED', 'LEAVE_DOCUMENT', OLD.id, NEW.document_no,
                 OLD.leave_hours, 'คืนโควต้าจากการเปลี่ยนสถานะหรือข้อมูลเอกสาร',
                 'SYSTEM', 'ระบบอัตโนมัติ');
        END IF;
        IF new_active AND NEW.leave_hours <> 0 THEN
            INSERT INTO public.leave_quota_movements
                (leave_quota_id, employee_id, leave_type_id, quota_year,
                 movement_type, source_type, source_id, reference_no,
                 change_hours, notes, action_by, action_by_name)
            VALUES
                (new_quota_id, NEW.creator_employee_id, NEW.leave_type_id, new_year,
                 'LEAVE_RESERVED', 'LEAVE_DOCUMENT', NEW.id, NEW.document_no,
                 -NEW.leave_hours, 'ตัดโควต้าจากการเปลี่ยนสถานะหรือข้อมูลเอกสาร',
                 'SYSTEM', 'ระบบอัตโนมัติ');
        END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_leave_document_quota_movement ON public.leave_documents;
CREATE TRIGGER trg_leave_document_quota_movement
AFTER INSERT OR UPDATE OF document_no, creator_employee_id, leave_type_id,
    leave_date, leave_hours, status ON public.leave_documents
FOR EACH ROW EXECUTE FUNCTION public.track_leave_document_quota_movement();

INSERT INTO public.application_pages
    (page_key, page_name, route_path, category_name, display_order)
VALUES
    ('LEAVE_QUOTA_MOVEMENTS', 'Movement โควต้าวันลา',
     '/leave/quota-movements', 'การลา', 65)
ON CONFLICT (page_key) DO UPDATE SET
    page_name = EXCLUDED.page_name,
    route_path = EXCLUDED.route_path,
    category_name = EXCLUDED.category_name,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

INSERT INTO public.application_page_actions
    (application_page_id, action_key, action_name, description, display_order)
SELECT id, 'VIEW_ALL', 'ดู Movement โควต้าทั้งหมด',
       'ดูรายการเพิ่ม ลด และคืนโควต้าวันลาของพนักงาน', 10
FROM public.application_pages WHERE page_key = 'LEAVE_QUOTA_MOVEMENTS'
ON CONFLICT (application_page_id, action_key) DO UPDATE SET
    action_name = EXCLUDED.action_name,
    description = EXCLUDED.description,
    display_order = EXCLUDED.display_order,
    is_active = TRUE;

-- Existing quota managers retain access to the movement page.
INSERT INTO public.employee_page_permissions
    (employee_id, application_page_id, can_access, updated_by, updated_by_name)
SELECT permission.employee_id, movement_page.id, permission.can_access,
       permission.updated_by, permission.updated_by_name
FROM public.employee_page_permissions permission
JOIN public.application_pages quota_page
  ON quota_page.id = permission.application_page_id
 AND quota_page.page_key = 'LEAVE_MANAGE_QUOTA'
CROSS JOIN public.application_pages movement_page
WHERE movement_page.page_key = 'LEAVE_QUOTA_MOVEMENTS'
ON CONFLICT (employee_id, application_page_id) DO NOTHING;

INSERT INTO public.app_role_page_permissions
    (app_role_id, application_page_id, can_access, updated_by, updated_by_name)
SELECT permission.app_role_id, movement_page.id, permission.can_access,
       permission.updated_by, permission.updated_by_name
FROM public.app_role_page_permissions permission
JOIN public.application_pages quota_page
  ON quota_page.id = permission.application_page_id
 AND quota_page.page_key = 'LEAVE_MANAGE_QUOTA'
CROSS JOIN public.application_pages movement_page
WHERE movement_page.page_key = 'LEAVE_QUOTA_MOVEMENTS'
ON CONFLICT (app_role_id, application_page_id) DO NOTHING;

COMMIT;
