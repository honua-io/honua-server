WITH publications AS (
    SELECT jsonb_set(
        jsonb_set(publication, '{metadata,id}', resource #> '{metadata,name}'),
        '{metadata,name}', resource #> '{metadata,name}') AS document
    FROM cite_source_graph AS source(document)
    CROSS JOIN LATERAL jsonb_array_elements(source.document -> 'publications') AS publication
    JOIN LATERAL jsonb_array_elements(source.document -> 'resources') AS resource
        ON resource #>> '{metadata,id}' = publication ->> 'resourceId'
    WHERE publication ->> 'publicationType' = 'ogc-collection'
), revision AS (
    SELECT COALESCE(MAX(revision), 0) + 1 AS value
    FROM honua.metadata_v2_snapshots
    WHERE environment = 'Test'
), graph AS (
    SELECT jsonb_set(jsonb_set(source.document, '{publications}',
        (SELECT jsonb_agg(document ORDER BY document #>> '{metadata,id}') FROM publications)),
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
