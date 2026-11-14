-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

-- Publish only references to accepted operations. The trigger shares the operation transaction,
-- so rollback also restores the realtime head and commit makes the reference visible to peers.
CREATE OR REPLACE FUNCTION $HonuaSchema$.publish_saved_map_operation_to_realtime()
RETURNS TRIGGER
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
    PERFORM $HonuaSchema$.realtime_append('collab-ops', jsonb_build_array(jsonb_build_object(
        'scope_key', NEW.map_id,
        'kind', 'operation-appended',
        'payload', jsonb_build_object(
            'operationId', NEW.operation_id,
            'mapId', NEW.map_id,
            'serverCursor', NEW.server_cursor,
            'baseCursor', NEW.base_cursor,
            'actorId', NEW.actor_id,
            'kind', NEW.operation_kind))));
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_publish_saved_map_operation_to_realtime
    AFTER INSERT ON $HonuaSchema$.saved_map_operations
    FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.publish_saved_map_operation_to_realtime();
