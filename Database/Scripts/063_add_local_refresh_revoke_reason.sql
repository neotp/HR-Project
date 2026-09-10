BEGIN;

ALTER TABLE public.local_refresh_tokens
    ADD COLUMN IF NOT EXISTS revoke_reason VARCHAR(30);

UPDATE public.local_refresh_tokens
SET revoke_reason = 'LEGACY'
WHERE revoked_at IS NOT NULL AND revoke_reason IS NULL;

COMMIT;
