BEGIN;

CREATE TABLE IF NOT EXISTS public.attendance_recalculation_queue
(
    employee_id    VARCHAR(50) NOT NULL,
    work_date      DATE        NOT NULL,
    reason         VARCHAR(100) NOT NULL DEFAULT 'LEAVE_DOCUMENT_CHANGED',
    requested_at   TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    attempts       INTEGER     NOT NULL DEFAULT 0,
    last_error     TEXT,
    PRIMARY KEY (employee_id, work_date)
);

CREATE OR REPLACE FUNCTION public.queue_attendance_recalculation_from_leave()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    old_effective BOOLEAN := FALSE;
    new_effective BOOLEAN := FALSE;
    effective_values_changed BOOLEAN := FALSE;
BEGIN
    IF TG_OP <> 'INSERT' THEN
        old_effective := OLD.status IN ('APPROVED', 'EDIT_REQUESTED');
    END IF;
    IF TG_OP <> 'DELETE' THEN
        new_effective := NEW.status IN ('APPROVED', 'EDIT_REQUESTED');
    END IF;
    IF TG_OP = 'UPDATE' THEN
        effective_values_changed :=
            OLD.creator_employee_id IS DISTINCT FROM NEW.creator_employee_id OR
            OLD.leave_date IS DISTINCT FROM NEW.leave_date OR
            OLD.start_time IS DISTINCT FROM NEW.start_time OR
            OLD.leave_hours IS DISTINCT FROM NEW.leave_hours;
    END IF;

    IF old_effective AND
       (NOT new_effective OR effective_values_changed) THEN
        INSERT INTO public.attendance_recalculation_queue(employee_id, work_date)
        VALUES (OLD.creator_employee_id, OLD.leave_date)
        ON CONFLICT (employee_id, work_date) DO UPDATE SET
            reason = 'LEAVE_DOCUMENT_CHANGED', requested_at = CURRENT_TIMESTAMP,
            attempts = 0, last_error = NULL;
    END IF;

    IF new_effective AND
       (NOT old_effective OR effective_values_changed) THEN
        INSERT INTO public.attendance_recalculation_queue(employee_id, work_date)
        VALUES (NEW.creator_employee_id, NEW.leave_date)
        ON CONFLICT (employee_id, work_date) DO UPDATE SET
            reason = 'LEAVE_DOCUMENT_CHANGED', requested_at = CURRENT_TIMESTAMP,
            attempts = 0, last_error = NULL;
    END IF;

    IF TG_OP = 'DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_leave_document_attendance_recalculation
    ON public.leave_documents;
CREATE TRIGGER trg_leave_document_attendance_recalculation
AFTER INSERT OR DELETE OR UPDATE OF
    status, creator_employee_id, leave_date, start_time, leave_hours
ON public.leave_documents
FOR EACH ROW
EXECUTE FUNCTION public.queue_attendance_recalculation_from_leave();

COMMIT;
