BEGIN;

-- Existing documents are treated as advance leave. Users can edit the kind
-- later when the document is still pending approval.
ALTER TABLE public.leave_documents
    ADD COLUMN IF NOT EXISTS leave_kind VARCHAR(30) NOT NULL DEFAULT 'ADVANCE';

ALTER TABLE public.leave_edit_requests
    ADD COLUMN IF NOT EXISTS requested_leave_kind VARCHAR(30) NOT NULL DEFAULT 'ADVANCE';

ALTER TABLE public.leave_documents
    DROP CONSTRAINT IF EXISTS ck_leave_documents_kind;

ALTER TABLE public.leave_documents
    ADD CONSTRAINT ck_leave_documents_kind
    CHECK (leave_kind IN ('ADVANCE', 'RETROACTIVE'));

ALTER TABLE public.leave_edit_requests
    DROP CONSTRAINT IF EXISTS ck_leave_edit_requests_kind;

ALTER TABLE public.leave_edit_requests
    ADD CONSTRAINT ck_leave_edit_requests_kind
    CHECK (requested_leave_kind IN ('ADVANCE', 'RETROACTIVE'));

-- Leave kind belongs to each document, not to the leave-type master.
ALTER TABLE public.leave_types
    DROP CONSTRAINT IF EXISTS ck_leave_types_kind;

ALTER TABLE public.leave_types
    DROP COLUMN IF EXISTS leave_kind;

COMMIT;
