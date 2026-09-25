BEGIN;

UPDATE public.application_pages
SET page_name = 'รออนุมัติขอแก้ไขเอกสาร'
WHERE page_key = 'EMPLOYEE_EDIT_REQUESTS';

CREATE TABLE IF NOT EXISTS public.employee_edit_request_comments
(
    id                       BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_edit_request_id BIGINT       NOT NULL
        REFERENCES public.employee_edit_requests(id) ON DELETE CASCADE,
    comment_text             TEXT         NOT NULL,
    commented_by             VARCHAR(50)  NOT NULL,
    commented_by_name        VARCHAR(200) NOT NULL,
    commented_at             TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_active                BOOLEAN      NOT NULL DEFAULT TRUE,
    CONSTRAINT ck_employee_edit_request_comment_not_blank CHECK (BTRIM(comment_text) <> ''),
    CONSTRAINT ck_employee_edit_request_comment_length CHECK (char_length(comment_text) <= 4000)
);

CREATE INDEX IF NOT EXISTS ix_employee_edit_request_comments_request
    ON public.employee_edit_request_comments
       (employee_edit_request_id, commented_at, id)
    WHERE is_active = TRUE;

COMMENT ON TABLE public.employee_edit_request_comments IS
    'Conversation attached to an employee edit request.';

COMMIT;
