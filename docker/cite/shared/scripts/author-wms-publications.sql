-- WMS advertises each layer under its publication's service-local identifier.
-- The v1-compat projection derives that identifier from the numeric layer index,
-- but the WMS 1.3 ETS addresses the CITE layers by name (BasicPolygons, Lakes, ...).
-- Author a Metadata v2 snapshot whose publications carry the CITE layer name,
-- without its namespace prefix, as the service-local identifier. Layer indices are
-- unchanged, so index-addressed routes keep their numbers.
-- Capabilities publish catalog metadata rather than placeholders, so the services
-- also declare the publisher, contact point and describedby link the ETS
-- recommendations look for; every layer inherits the link from its service.
-- Expects the temp table cite_source_graph(document) built from the compat projection.
WITH publications AS (
    SELECT item.ordinality,
        CASE
            WHEN layer.layer_name IS NULL THEN item.publication
            ELSE jsonb_set(item.publication, '{serviceLocalId}',
                to_jsonb(regexp_replace(layer.layer_name, '^.*:', '')))
        END AS document
    FROM cite_source_graph AS source(document)
    CROSS JOIN LATERAL jsonb_array_elements(source.document -> 'publications')
        WITH ORDINALITY AS item(publication, ordinality)
    LEFT JOIN honua.layers AS layer
        ON layer.layer_id = (item.publication ->> 'layerIndex')::int
), services AS (
    SELECT item.ordinality,
        jsonb_set(item.service, '{metadata}', COALESCE(item.service -> 'metadata', '{}'::jsonb)
            || jsonb_build_object(
                'publisher', 'Honua CITE fixture',
                'contactPoint', jsonb_build_object(
                    'name', 'CITE fixture operator',
                    'email', 'cite@example.com',
                    'url', 'https://example.com/cite'),
                'links', COALESCE(item.service #> '{metadata,links}', '[]'::jsonb)
                    || jsonb_build_array(jsonb_build_object(
                        'rel', 'describedby',
                        'type', 'text/xml',
                        'href', 'http://honua-server:8080/rest/services/cite/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0')))) AS document
    FROM cite_source_graph AS source(document)
    CROSS JOIN LATERAL jsonb_array_elements(source.document -> 'services')
        WITH ORDINALITY AS item(service, ordinality)
), revision AS (
    SELECT COALESCE(MAX(revision), 0) + 1 AS value
    FROM honua.metadata_v2_snapshots
    WHERE environment = 'Test'
), graph AS (
    SELECT jsonb_set(jsonb_set(jsonb_set(source.document, '{publications}',
        (SELECT jsonb_agg(document ORDER BY ordinality) FROM publications)),
        '{services}', (SELECT jsonb_agg(document ORDER BY ordinality) FROM services)),
        '{revision}', to_jsonb(revision.value)) AS document
    FROM cite_source_graph AS source(document), revision
), persisted AS (
    INSERT INTO honua.metadata_v2_snapshots (
        environment, revision, schema_version, api_version, document, etag, generated_at)
    SELECT document ->> 'environment', (document ->> 'revision')::bigint,
        document ->> 'schemaVersion', document ->> 'apiVersion', document,
        '"' || md5(document::text) || '"', NOW()
    FROM graph
    RETURNING environment, revision, etag
)
INSERT INTO honua.metadata_v2_current (environment, revision, etag)
SELECT environment, revision, etag FROM persisted
ON CONFLICT (environment) DO UPDATE SET
    revision = EXCLUDED.revision,
    etag = EXCLUDED.etag,
    activated_at = NOW();
