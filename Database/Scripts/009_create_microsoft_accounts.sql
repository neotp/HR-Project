BEGIN;

CREATE TABLE IF NOT EXISTS public.microsoft_accounts
(
    id                          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id                   VARCHAR(50)  NOT NULL,
    entra_object_id             VARCHAR(80)  NOT NULL,
    employee_email              VARCHAR(320) NOT NULL,
    employee_email_normalized   VARCHAR(320) NOT NULL,
    employee_id                 VARCHAR(50),
    display_name                VARCHAR(200) NOT NULL,
    user_principal_name         VARCHAR(320),
    is_active                   BOOLEAN      NOT NULL DEFAULT TRUE,
    first_linked_at             TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_sign_in_at             TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                  TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT ux_microsoft_accounts_object
        UNIQUE (tenant_id, entra_object_id),
    CONSTRAINT ux_microsoft_accounts_email
        UNIQUE (tenant_id, employee_email_normalized)
);

COMMENT ON TABLE public.microsoft_accounts IS
    'Microsoft Entra identities linked to the external employee directory by normalized email.';
COMMENT ON COLUMN public.microsoft_accounts.employee_id IS
    'Nullable external employee key; populated when an employee is matched by email.';

CREATE INDEX IF NOT EXISTS ix_microsoft_accounts_employee
    ON public.microsoft_accounts(employee_id);

DROP TRIGGER IF EXISTS trg_microsoft_accounts_updated_at
    ON public.microsoft_accounts;
CREATE TRIGGER trg_microsoft_accounts_updated_at
    BEFORE UPDATE ON public.microsoft_accounts
    FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();

COMMIT;
