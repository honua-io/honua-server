ALTER TABLE honua.alert_dispatch
    ADD COLUMN IF NOT EXISTS claim_token UUID;

COMMENT ON COLUMN honua.alert_dispatch.claim_token IS
    'Opaque fencing token for the worker that currently owns a Processing dispatch';
