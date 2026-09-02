BEGIN;

ALTER TABLE public.leave_document_history
    DROP CONSTRAINT IF EXISTS ck_leave_history_action;

ALTER TABLE public.leave_document_history
    ADD CONSTRAINT ck_leave_history_action
    CHECK (action IN
    (
        'CREATE_DOCUMENT',
        'APPROVE',
        'REJECT',
        'EDIT',
        'CANCEL',
        'REQUEST_EDIT',
        'CANCEL_EDIT_REQUEST',
        'APPROVE_EDIT_REQUEST',
        'REQUEST_CANCEL',
        'CANCEL_CANCEL_REQUEST',
        'APPROVE_CANCEL_REQUEST',
        'REJECT_CANCEL_REQUEST'
    ));

COMMIT;
