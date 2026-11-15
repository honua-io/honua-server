-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Scene datasets and row and field-mask policies shape responses that every server task caches.
-- A change to any of them advances the shared configuration generation, which keys those cached
-- responses, so no task keeps serving a response shaped by the previous rows. The tables stay in
-- the honua schema; the generation lives in the metadata schema.
CREATE OR REPLACE TRIGGER scene_datasets_configuration_generation AFTER INSERT OR UPDATE OR DELETE
    ON honua.scene_datasets FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.advance_configuration_generation();

CREATE OR REPLACE TRIGGER rbac_rls_policies_configuration_generation AFTER INSERT OR UPDATE OR DELETE
    ON honua.rbac_rls_policies FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.advance_configuration_generation();

CREATE OR REPLACE TRIGGER rbac_field_mask_policies_configuration_generation AFTER INSERT OR UPDATE OR DELETE
    ON honua.rbac_field_mask_policies FOR EACH ROW
    EXECUTE FUNCTION $HonuaSchema$.advance_configuration_generation();
