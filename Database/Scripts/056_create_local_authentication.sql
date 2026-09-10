BEGIN;

CREATE TABLE IF NOT EXISTS public.local_user_accounts
(
    id                    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    employee_id           VARCHAR(50) NOT NULL REFERENCES public.employees(employee_code),
    username              VARCHAR(100) NOT NULL,
    normalized_username   VARCHAR(100) NOT NULL,
    password_hash         TEXT NOT NULL,
    is_active             BOOLEAN NOT NULL DEFAULT TRUE,
    must_change_password  BOOLEAN NOT NULL DEFAULT FALSE,
    failed_access_count   INTEGER NOT NULL DEFAULT 0,
    lockout_end           TIMESTAMPTZ,
    security_stamp        UUID NOT NULL DEFAULT gen_random_uuid(),
    password_changed_at   TIMESTAMPTZ,
    last_login_at         TIMESTAMPTZ,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_by            VARCHAR(50) NOT NULL,
    updated_at            TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT ux_local_user_accounts_employee UNIQUE (employee_id),
    CONSTRAINT ux_local_user_accounts_username UNIQUE (normalized_username),
    CONSTRAINT ck_local_user_accounts_failed_count CHECK (failed_access_count >= 0)
);

CREATE TABLE IF NOT EXISTS public.local_refresh_tokens
(
    id             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    local_user_id  UUID NOT NULL REFERENCES public.local_user_accounts(id) ON DELETE CASCADE,
    token_hash     CHAR(64) NOT NULL UNIQUE,
    expires_at     TIMESTAMPTZ NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    created_ip     VARCHAR(100),
    revoked_at     TIMESTAMPTZ,
    revoked_ip     VARCHAR(100),
    replaced_by_id BIGINT REFERENCES public.local_refresh_tokens(id)
);

CREATE INDEX IF NOT EXISTS ix_local_refresh_tokens_user_active
    ON public.local_refresh_tokens (local_user_id, expires_at DESC)
    WHERE revoked_at IS NULL;

CREATE TABLE IF NOT EXISTS public.local_login_audit
(
    id             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    local_user_id  UUID REFERENCES public.local_user_accounts(id) ON DELETE SET NULL,
    username       VARCHAR(100) NOT NULL,
    employee_id    VARCHAR(50),
    is_success     BOOLEAN NOT NULL,
    failure_reason VARCHAR(100),
    ip_address     VARCHAR(100),
    user_agent     VARCHAR(500),
    attempted_at   TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS ix_local_login_audit_attempted
    ON public.local_login_audit (attempted_at DESC);

ALTER TABLE public.local_user_accounts
    ALTER COLUMN must_change_password SET DEFAULT FALSE;

COMMIT;
