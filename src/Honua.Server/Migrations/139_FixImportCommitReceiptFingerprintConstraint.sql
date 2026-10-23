-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Preserve the applied receipt migration and correct its prefix-length check.
-- Writers and readers already use the complete canonical versioned fingerprint.
-- honua:compatibility-review reason=Corrects the receipt check to the existing canonical fingerprint format; existing databases retain the normal reviewed contract-migration gate.
ALTER TABLE $HonuaSchema$.import_commit_receipt
    DROP CONSTRAINT import_commit_receipt_intent;

ALTER TABLE $HonuaSchema$.import_commit_receipt
    ADD CONSTRAINT import_commit_receipt_intent CHECK (
        char_length(intent_fingerprint) = char_length('v1:sha256:') + 64 AND
        left(intent_fingerprint, char_length('v1:sha256:')) = 'v1:sha256:' AND
        substring(intent_fingerprint FROM char_length('v1:sha256:') + 1) !~ '[^0-9a-f]');
