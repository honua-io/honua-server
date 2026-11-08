ALTER TABLE honua.migration_batch_children
    ADD COLUMN IF NOT EXISTS publication_target JSONB;
