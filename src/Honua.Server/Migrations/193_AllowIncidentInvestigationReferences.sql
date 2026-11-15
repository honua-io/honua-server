ALTER TABLE $HonuaSchema$.investigation_links
    DROP CONSTRAINT investigation_links_valid_kind;
ALTER TABLE $HonuaSchema$.investigation_links
    ADD CONSTRAINT investigation_links_valid_kind CHECK (resource_kind BETWEEN 1 AND 5);

ALTER TABLE $HonuaSchema$.investigation_pins
    DROP CONSTRAINT investigation_pins_valid_kind;
ALTER TABLE $HonuaSchema$.investigation_pins
    ADD CONSTRAINT investigation_pins_valid_kind CHECK (event_kind BETWEEN 1 AND 8);
