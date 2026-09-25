ALTER TABLE public.employee_activity_history
    ADD COLUMN IF NOT EXISTS changes_json JSONB NOT NULL DEFAULT '[]'::jsonb;

COMMENT ON COLUMN public.employee_activity_history.changes_json IS
    'Field-level old/new values captured for direct employee updates';
