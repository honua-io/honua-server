ALTER TABLE honua.alert_dispatch
    ADD COLUMN IF NOT EXISTS claim_token UUID;

COMMENT ON COLUMN honua.alert_dispatch.claim_token IS
    'Opaque fencing token for the worker that currently owns a Processing dispatch';

-- Enforce fencing in the database as well as in the current worker's UPDATE
-- predicates. During a rolling deployment, an older worker can otherwise claim
-- without a token or complete a claim after a newer worker has won the lease.
CREATE OR REPLACE FUNCTION honua.require_fenced_alert_dispatch_transition()
RETURNS trigger AS $$
BEGIN
    IF NEW.status = 1 AND NEW.claim_token IS NULL THEN
        RAISE EXCEPTION 'Processing alert dispatches require a claim token';
    END IF;

    IF NEW.status IN (2, 3, 4)
        AND (OLD.status <> 1 OR OLD.claim_token IS NULL OR NEW.claim_token IS NOT NULL) THEN
        RAISE EXCEPTION 'Alert dispatch terminal transitions require a fenced Processing claim';
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_require_fenced_alert_dispatch_transition
    ON honua.alert_dispatch;

CREATE TRIGGER trg_require_fenced_alert_dispatch_transition
    BEFORE UPDATE ON honua.alert_dispatch
    FOR EACH ROW
    EXECUTE FUNCTION honua.require_fenced_alert_dispatch_transition();
