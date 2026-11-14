-- Capture observations in the edit transaction, including direct SQL inserts. The application
-- stamps exact tenant/schema identity in transaction-local context; unscoped SQL stays unscoped.
-- Schema names identify physical tables only and never stand in for a tenant identity.
CREATE OR REPLACE FUNCTION $HonuaSchema$.capture_observation_realtime_events()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    context_text text := current_setting('honua.observation_scope', true);
    context jsonb;
    event_scope jsonb;
    observation record;
    stream_id bigint;
    batch jsonb := '[]'::jsonb;
    item jsonb;
    candidate jsonb;
    event_payload jsonb;
BEGIN
    IF context_text IS NULL OR context_text = '' THEN
        event_scope := jsonb_build_object('tenantId', NULL, 'schema', NULL);
    ELSE
        context := context_text::jsonb;
        IF jsonb_typeof(context) <> 'object'
           OR context->>'tableSchema' IS DISTINCT FROM TG_TABLE_SCHEMA
           OR NOT context ? 'tenantId' OR NOT context ? 'schema'
           OR jsonb_typeof(context->'tenantId') NOT IN ('string', 'null')
           OR jsonb_typeof(context->'schema') NOT IN ('string', 'null') THEN
            RAISE EXCEPTION 'Observation scope does not match the inserted table' USING ERRCODE = '22023';
        END IF;
        event_scope := jsonb_build_object('tenantId', context->'tenantId', 'schema', context->'schema');
    END IF;

    FOR observation IN
        SELECT * FROM inserted_observations
        ORDER BY COALESCE(datastream_reference_id, datastream_id), id, phenomenon_time
    LOOP
        IF stream_id IS NOT NULL AND stream_id <> COALESCE(observation.datastream_reference_id, observation.datastream_id) THEN
            PERFORM $HonuaSchema$.realtime_append('observations', jsonb_build_array(jsonb_build_object(
                'scope_key', stream_id::text, 'kind', 'observation.created',
                'payload', jsonb_build_object('scope', event_scope, 'datastreamId', stream_id, 'observations', batch))));
            batch := '[]'::jsonb;
        END IF;
        stream_id := COALESCE(observation.datastream_reference_id, observation.datastream_id);
        item := jsonb_build_object(
            'id', observation.id,
            'phenomenonTime', CASE WHEN observation.phenomenon_time_end IS NULL THEN to_jsonb(observation.phenomenon_time)
                ELSE to_jsonb((to_jsonb(observation.phenomenon_time) #>> '{}') || '/' || (to_jsonb(observation.phenomenon_time_end) #>> '{}')) END,
            'resultTime', observation.result_time,
            'result', COALESCE(observation.result_json, to_jsonb(observation.result), 'null'::jsonb),
            'featureOfInterestId', COALESCE(observation.feature_of_interest_reference_id, observation.feature_of_interest_id));
        candidate := batch || jsonb_build_array(item);
        event_payload := jsonb_build_object('scope', event_scope, 'datastreamId', stream_id, 'observations', candidate);
        IF jsonb_array_length(batch) > 0 AND (jsonb_array_length(candidate) > 1000 OR octet_length(event_payload::text) > 65536) THEN
            PERFORM $HonuaSchema$.realtime_append('observations', jsonb_build_array(jsonb_build_object(
                'scope_key', stream_id::text, 'kind', 'observation.created',
                'payload', jsonb_build_object('scope', event_scope, 'datastreamId', stream_id, 'observations', batch))));
            candidate := jsonb_build_array(item);
            event_payload := jsonb_build_object('scope', event_scope, 'datastreamId', stream_id, 'observations', candidate);
        END IF;
        IF octet_length(event_payload::text) > 65536 THEN
            RAISE EXCEPTION 'Observation event payload exceeds 65536 bytes' USING ERRCODE = '54000';
        END IF;
        batch := candidate;
    END LOOP;
    IF jsonb_array_length(batch) > 0 THEN
        PERFORM $HonuaSchema$.realtime_append('observations', jsonb_build_array(jsonb_build_object(
            'scope_key', stream_id::text, 'kind', 'observation.created',
            'payload', jsonb_build_object('scope', event_scope, 'datastreamId', stream_id, 'observations', batch))));
    END IF;
    RETURN NULL;
END;
$function$;

DO $install$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_catalog.pg_trigger
        WHERE tgrelid = '$HonuaSchema$.sta_observation'::regclass
          AND tgname = 'trigger_capture_observation_realtime_events'
    ) THEN
        CREATE TRIGGER trigger_capture_observation_realtime_events
        AFTER INSERT ON $HonuaSchema$.sta_observation
        REFERENCING NEW TABLE AS inserted_observations
        FOR EACH STATEMENT EXECUTE FUNCTION $HonuaSchema$.capture_observation_realtime_events();
    END IF;
END;
$install$;
