BEGIN;

ALTER TABLE public.application_pages
    ADD COLUMN IF NOT EXISTS is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    ADD COLUMN IF NOT EXISTS availability_updated_by VARCHAR(50),
    ADD COLUMN IF NOT EXISTS availability_updated_by_name VARCHAR(200),
    ADD COLUMN IF NOT EXISTS availability_updated_at TIMESTAMPTZ;

COMMENT ON COLUMN public.application_pages.is_active IS
    'Defines whether the page is registered in the application.';

COMMENT ON COLUMN public.application_pages.is_enabled IS
    'Global operational switch. FALSE means the page is temporarily closed for every user.';

-- The permission-management page must remain available so an administrator
-- always has a way to reopen other pages from the UI.
UPDATE public.application_pages
SET is_enabled = TRUE
WHERE page_key = 'PERMISSIONS';

COMMIT;
