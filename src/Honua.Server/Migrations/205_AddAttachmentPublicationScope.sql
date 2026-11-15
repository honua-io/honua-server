ALTER TABLE $HonuaSchema$.attachment_upload_sessions
    ADD COLUMN IF NOT EXISTS published_layer_id INTEGER
    CHECK (published_layer_id >= 0);
