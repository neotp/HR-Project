BEGIN;

ALTER TABLE public.leave_comment_notifications
    DROP CONSTRAINT IF EXISTS ck_leave_comment_recipient_type;

ALTER TABLE public.leave_comment_notifications
    ADD CONSTRAINT ck_leave_comment_recipient_type
        CHECK (recipient_type IN
            ('REQUESTER', 'APPROVER', 'BOSS', 'LEAVE_APPROVER', 'WORKFLOW', 'ADDITIONAL'));

COMMIT;
