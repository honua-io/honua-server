-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Dataset-backed travel-profile cost mappings. The default is deliberately
-- driving-only, preserving every existing topology and deployment unchanged.
ALTER TABLE honua.network_datasets
    ADD COLUMN IF NOT EXISTS travel_profiles JSONB NOT NULL DEFAULT
        '[{"name":"driving","forwardCostColumn":"cost","reverseCostColumn":"reverse_cost"}]'::jsonb;

COMMENT ON COLUMN honua.network_datasets.travel_profiles IS
    'Validated travel profile names and forward/reverse edge impedance columns. Profiles are advertised only when both columns exist.';
