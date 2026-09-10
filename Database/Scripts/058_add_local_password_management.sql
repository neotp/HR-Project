BEGIN;

ALTER TABLE public.local_user_accounts
    ALTER COLUMN must_change_password SET DEFAULT TRUE;

CREATE TABLE IF NOT EXISTS public.local_password_reset_requests
(
    id             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    local_user_id  UUID NOT NULL REFERENCES public.local_user_accounts(id) ON DELETE CASCADE,
    employee_id    VARCHAR(50) NOT NULL,
    status         VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    requested_at   TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    requested_ip   VARCHAR(100),
    resolved_at    TIMESTAMPTZ,
    resolved_by    VARCHAR(50),
    CONSTRAINT ck_local_password_reset_status
        CHECK (status IN ('PENDING', 'COMPLETED', 'CANCELLED'))
);

CREATE INDEX IF NOT EXISTS ix_local_password_reset_requests_pending
    ON public.local_password_reset_requests (requested_at, id)
    WHERE status = 'PENDING';

CREATE UNIQUE INDEX IF NOT EXISTS ux_local_password_reset_requests_pending_user
    ON public.local_password_reset_requests (local_user_id)
    WHERE status = 'PENDING';

COMMIT;
