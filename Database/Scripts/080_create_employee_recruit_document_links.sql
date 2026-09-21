BEGIN;

CREATE TABLE IF NOT EXISTS public.employee_recruit_document_links
(
    id              BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    employee_id     BIGINT NOT NULL REFERENCES public.employees(id) ON DELETE CASCADE,
    link_url        VARCHAR(2048) NOT NULL,
    added_by        VARCHAR(50) NOT NULL,
    added_by_name   VARCHAR(200) NOT NULL,
    added_at        TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    deleted_by      VARCHAR(50),
    deleted_by_name VARCHAR(200),
    deleted_at      TIMESTAMPTZ,
    CONSTRAINT ck_employee_recruit_document_links_url
        CHECK (link_url ~* '^https?://')
);

CREATE INDEX IF NOT EXISTS ix_employee_recruit_document_links_employee
    ON public.employee_recruit_document_links (employee_id, added_at DESC, id DESC)
    WHERE is_active = TRUE;

CREATE UNIQUE INDEX IF NOT EXISTS ux_employee_recruit_document_links_active_url
    ON public.employee_recruit_document_links (employee_id, LOWER(link_url))
    WHERE is_active = TRUE;

COMMIT;
