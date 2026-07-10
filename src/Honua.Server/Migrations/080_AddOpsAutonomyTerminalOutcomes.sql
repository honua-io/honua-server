-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Outcome 3 records a post-invocation state that verification/compensation could not
-- determine safely. Outcome 4 records cancellation after actuator invocation began.
-- Both are terminal and count in the failed track-record bucket; neither may ever be
-- interpreted as an autonomous success.
ALTER TABLE honua.ops_autonomy_action_log
    DROP CONSTRAINT IF EXISTS ops_autonomy_action_valid_outcome;

ALTER TABLE honua.ops_autonomy_action_log
    ADD CONSTRAINT ops_autonomy_action_valid_outcome
    CHECK (outcome IS NULL OR outcome IN (0, 1, 2, 3, 4));

COMMENT ON COLUMN honua.ops_autonomy_action_log.outcome IS
    'Terminal autonomy outcome: 0=succeeded, 1=failed, 2=rolled back, 3=indeterminate/manual intervention required, 4=canceled after invocation.';
