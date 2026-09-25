BEGIN;

CREATE TABLE IF NOT EXISTS public.attendance_comment_attachments
(
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    attendance_comment_id BIGINT NOT NULL REFERENCES public.attendance_comments(id) ON DELETE CASCADE,
    original_file_name VARCHAR(255) NOT NULL,
    content_type VARCHAR(100) NOT NULL CHECK (content_type IN ('image/png','image/jpeg','image/webp')),
    file_size_bytes BIGINT NOT NULL CHECK (file_size_bytes BETWEEN 1 AND 3145728),
    file_content BYTEA NOT NULL,
    uploaded_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (OCTET_LENGTH(file_content)=file_size_bytes)
);
CREATE INDEX IF NOT EXISTS ix_attendance_comment_attachments_comment
    ON public.attendance_comment_attachments(attendance_comment_id,uploaded_at,id);

CREATE TABLE IF NOT EXISTS public.employee_edit_request_comment_attachments
(
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_edit_request_comment_id BIGINT NOT NULL REFERENCES public.employee_edit_request_comments(id) ON DELETE CASCADE,
    original_file_name VARCHAR(255) NOT NULL,
    content_type VARCHAR(100) NOT NULL CHECK (content_type IN ('image/png','image/jpeg','image/webp')),
    file_size_bytes BIGINT NOT NULL CHECK (file_size_bytes BETWEEN 1 AND 3145728),
    file_content BYTEA NOT NULL,
    uploaded_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (OCTET_LENGTH(file_content)=file_size_bytes)
);
CREATE INDEX IF NOT EXISTS ix_employee_edit_request_comment_attachments_comment
    ON public.employee_edit_request_comment_attachments(employee_edit_request_comment_id,uploaded_at,id);

CREATE TABLE IF NOT EXISTS public.leave_quota_request_comment_attachments
(
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    leave_quota_request_comment_id BIGINT NOT NULL REFERENCES public.leave_quota_request_comments(id) ON DELETE CASCADE,
    original_file_name VARCHAR(255) NOT NULL,
    content_type VARCHAR(100) NOT NULL CHECK (content_type IN ('image/png','image/jpeg','image/webp')),
    file_size_bytes BIGINT NOT NULL CHECK (file_size_bytes BETWEEN 1 AND 3145728),
    file_content BYTEA NOT NULL,
    uploaded_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CHECK (OCTET_LENGTH(file_content)=file_size_bytes)
);
CREATE INDEX IF NOT EXISTS ix_leave_quota_request_comment_attachments_comment
    ON public.leave_quota_request_comment_attachments(leave_quota_request_comment_id,uploaded_at,id);

COMMIT;
