BEGIN;

CREATE SCHEMA IF NOT EXISTS public;

-- Master data for leave categories shown in dropdowns.
CREATE TABLE IF NOT EXISTS public.leave_types
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code            VARCHAR(30)  NOT NULL UNIQUE,
    name_th         VARCHAR(150) NOT NULL,
    default_hours   NUMERIC(8,2) NOT NULL DEFAULT 0,
    is_active       BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ck_leave_types_default_hours CHECK (default_hours >= 0)
);

-- Main leave document. Employee IDs are external identifiers until the
-- employee module/table is introduced, so there is intentionally no FK yet.
CREATE TABLE IF NOT EXISTS public.leave_documents
(
    id                      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    document_no             VARCHAR(30)  NOT NULL UNIQUE,
    creator_employee_id     VARCHAR(50)  NOT NULL,
    creator_name            VARCHAR(200) NOT NULL,
    creator_department      VARCHAR(200) NOT NULL,
    approver_employee_id    VARCHAR(50),
    approver_name           VARCHAR(200) NOT NULL,
    leave_type_id           BIGINT       NOT NULL,
    leave_kind              VARCHAR(30)  NOT NULL,
    leave_date              DATE         NOT NULL,
    start_time              TIME         NOT NULL,
    leave_hours             NUMERIC(5,2) NOT NULL,
    leave_reason            TEXT         NOT NULL,
    status                  VARCHAR(40)  NOT NULL DEFAULT 'PENDING_APPROVAL',
    created_at              TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at              TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    approved_at             TIMESTAMPTZ,
    rejected_at             TIMESTAMPTZ,
    cancelled_at            TIMESTAMPTZ,

    CONSTRAINT fk_leave_documents_leave_type
        FOREIGN KEY (leave_type_id) REFERENCES public.leave_types(id),
    CONSTRAINT ck_leave_documents_hours
        CHECK (leave_hours > 0 AND leave_hours <= 24),
    CONSTRAINT ck_leave_documents_kind
        CHECK (leave_kind IN ('ADVANCE', 'RETROACTIVE')),
    CONSTRAINT ck_leave_documents_status
        CHECK (status IN
        (
            'PENDING_APPROVAL',
            'APPROVED',
            'REJECTED',
            'CANCELLED',
            'EDIT_REQUESTED'
        ))
);

-- Attachment metadata and binary content. Legacy path columns remain nullable
-- so databases migrated from file-system storage can retain old records.
CREATE TABLE IF NOT EXISTS public.leave_document_attachments
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_document_id   BIGINT        NOT NULL,
    original_file_name  VARCHAR(255)  NOT NULL,
    stored_file_name    VARCHAR(255),
    storage_path        TEXT,
    content_type        VARCHAR(150),
    file_size_bytes     BIGINT        NOT NULL,
    file_content        BYTEA,
    leave_edit_request_id BIGINT,
    uploaded_by         VARCHAR(50)   NOT NULL,
    uploaded_at         TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_leave_attachments_document
        FOREIGN KEY (leave_document_id)
        REFERENCES public.leave_documents(id) ON DELETE CASCADE,
    CONSTRAINT ck_leave_attachments_file_size
        CHECK (file_size_bytes > 0 AND file_size_bytes <= 10485760),
    CONSTRAINT ck_leave_attachments_content_size
        CHECK (file_content IS NULL OR OCTET_LENGTH(file_content) = file_size_bytes)
);

-- Requested values are kept separately. The main leave document must not be
-- updated until this request reaches APPROVED.
CREATE TABLE IF NOT EXISTS public.leave_edit_requests
(
    id                      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_document_id       BIGINT       NOT NULL,
    requested_leave_type_id BIGINT       NOT NULL,
    requested_leave_kind    VARCHAR(30)  NOT NULL,
    requested_leave_date    DATE         NOT NULL,
    requested_start_time    TIME         NOT NULL,
    requested_leave_hours   NUMERIC(5,2) NOT NULL,
    requested_has_medical_certificate BOOLEAN,
    request_reason          TEXT         NOT NULL,
    status                  VARCHAR(30)  NOT NULL DEFAULT 'PENDING',
    requested_by            VARCHAR(50)  NOT NULL,
    requested_by_name       VARCHAR(200) NOT NULL,
    requested_at            TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    reviewed_by             VARCHAR(50),
    reviewed_by_name        VARCHAR(200),
    reviewed_at             TIMESTAMPTZ,
    review_remark           TEXT,
    updated_at              TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_leave_edit_requests_document
        FOREIGN KEY (leave_document_id)
        REFERENCES public.leave_documents(id) ON DELETE CASCADE,
    CONSTRAINT fk_leave_edit_requests_leave_type
        FOREIGN KEY (requested_leave_type_id)
        REFERENCES public.leave_types(id),
    CONSTRAINT ck_leave_edit_requests_hours
        CHECK (requested_leave_hours > 0 AND requested_leave_hours <= 24),
    CONSTRAINT ck_leave_edit_requests_kind
        CHECK (requested_leave_kind IN ('ADVANCE', 'RETROACTIVE')),
    CONSTRAINT ck_leave_edit_requests_status
        CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED'))
);

ALTER TABLE public.leave_document_attachments
    DROP CONSTRAINT IF EXISTS fk_leave_attachments_edit_request;
ALTER TABLE public.leave_document_attachments
    ADD CONSTRAINT fk_leave_attachments_edit_request
        FOREIGN KEY (leave_edit_request_id)
        REFERENCES public.leave_edit_requests(id) ON DELETE CASCADE;

-- Only one pending edit request may exist for a document at a time.
CREATE UNIQUE INDEX IF NOT EXISTS ux_leave_edit_requests_pending
    ON public.leave_edit_requests (leave_document_id)
    WHERE status = 'PENDING';

-- Audit trail. details_text is ready for human-readable text, while
-- details_json can preserve before/after values for reporting or APIs.
CREATE TABLE IF NOT EXISTS public.leave_document_history
(
    id                  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_document_id   BIGINT       NOT NULL,
    action              VARCHAR(40)  NOT NULL,
    details_text        TEXT,
    details_json        JSONB,
    action_by           VARCHAR(50)  NOT NULL,
    action_by_name      VARCHAR(200) NOT NULL,
    action_at           TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT fk_leave_history_document
        FOREIGN KEY (leave_document_id)
        REFERENCES public.leave_documents(id) ON DELETE CASCADE,
    CONSTRAINT ck_leave_history_action
        CHECK (action IN
        (
            'CREATE_DOCUMENT',
            'APPROVE',
            'REJECT',
            'EDIT',
            'CANCEL',
            'REQUEST_EDIT',
            'CANCEL_EDIT_REQUEST',
            'APPROVE_EDIT_REQUEST',
            'REQUEST_CANCEL',
            'CANCEL_CANCEL_REQUEST',
            'APPROVE_CANCEL_REQUEST',
            'REJECT_CANCEL_REQUEST'
        ))
);

CREATE INDEX IF NOT EXISTS ix_leave_documents_creator_status
    ON public.leave_documents (creator_employee_id, status);

CREATE INDEX IF NOT EXISTS ix_leave_documents_leave_date
    ON public.leave_documents (leave_date DESC);

CREATE INDEX IF NOT EXISTS ix_leave_documents_approver_status
    ON public.leave_documents (approver_employee_id, status);

CREATE INDEX IF NOT EXISTS ix_leave_history_document_action_at
    ON public.leave_document_history (leave_document_id, action_at DESC);

CREATE INDEX IF NOT EXISTS ix_leave_edit_requests_document_status
    ON public.leave_edit_requests (leave_document_id, status);

-- Keep updated_at consistent for all mutable leave tables.
CREATE OR REPLACE FUNCTION public.set_updated_at()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_leave_types_updated_at ON public.leave_types;
CREATE TRIGGER trg_leave_types_updated_at
BEFORE UPDATE ON public.leave_types
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

DROP TRIGGER IF EXISTS trg_leave_documents_updated_at ON public.leave_documents;
CREATE TRIGGER trg_leave_documents_updated_at
BEFORE UPDATE ON public.leave_documents
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

DROP TRIGGER IF EXISTS trg_leave_edit_requests_updated_at ON public.leave_edit_requests;
CREATE TRIGGER trg_leave_edit_requests_updated_at
BEFORE UPDATE ON public.leave_edit_requests
FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

INSERT INTO public.leave_types (code, name_th)
VALUES
    ('SICK',     'ลาป่วย'),
    ('VACATION', 'ลาพักร้อน'),
    ('PERSONAL',   'ลากิจไม่รับเงินเดือน'),
    ('UNPAID',     'ลาคลอด'),
    ('ORDINATION', 'ลาบวช')
ON CONFLICT (code)
DO UPDATE SET
    name_th = EXCLUDED.name_th,
    is_active = TRUE;

COMMIT;
