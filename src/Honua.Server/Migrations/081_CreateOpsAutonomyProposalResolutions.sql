-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Migration: 081_CreateOpsAutonomyProposalResolutions.sql
-- Description: Adds the proposal-id idempotency ledger required to account for
--              human approvals/rejections exactly once across retries and replicas
--              without storing execution payloads or reviewer-provided detail (#2631).

CREATE TABLE IF NOT EXISTS honua.ops_autonomy_proposal_resolutions (
    proposal_id TEXT        PRIMARY KEY,
    rule        TEXT        NOT NULL,
    resolution  SMALLINT    NOT NULL,
    resolved_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT ops_autonomy_resolution_valid_proposal CHECK (length(proposal_id) > 0),
    CONSTRAINT ops_autonomy_resolution_valid_rule CHECK (length(rule) > 0),
    CONSTRAINT ops_autonomy_resolution_valid_value CHECK (resolution IN (0, 1))
);

CREATE INDEX IF NOT EXISTS idx_ops_autonomy_proposal_resolution_rule_time
    ON honua.ops_autonomy_proposal_resolutions(rule, resolved_at DESC);

COMMENT ON TABLE honua.ops_autonomy_proposal_resolutions IS
    'Exactly-once ledger for finding-originated human proposal resolutions. resolution=0 approved; resolution=1 rejected. Contains no execution payload or reviewer detail.';
