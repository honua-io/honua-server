-- Extend the Compose fixture after the YAML seeds create the active catalog.
WITH updated_documents AS (
    SELECT snapshot.environment, snapshot.revision, jsonb_set(
        snapshot.document,
        '{services}',
        jsonb_agg(
            CASE WHEN service.value #>> '{metadata,id}' = 'svc-browser-compat-feature'
                THEN service.value || jsonb_build_object(
                    'protocols', service.value->'protocols' || '["VectorTileServer"]'::jsonb,
                    'enabledProtocols', service.value->'enabledProtocols' || '["VectorTileServer"]'::jsonb
                )
                ELSE service.value
            END ORDER BY service.position
        )
    ) AS document
    FROM honua.metadata_v2_snapshots AS snapshot
    JOIN honua.metadata_v2_current AS current
        USING (environment, revision)
    CROSS JOIN LATERAL jsonb_array_elements(snapshot.document->'services')
        WITH ORDINALITY AS service(value, position)
    GROUP BY snapshot.environment, snapshot.revision, snapshot.document
), updated_snapshots AS (
    UPDATE honua.metadata_v2_snapshots AS snapshot
    SET document = updated.document, etag = '"' || md5(updated.document::text) || '"'
    FROM updated_documents AS updated
    WHERE (snapshot.environment, snapshot.revision) = (updated.environment, updated.revision)
    RETURNING snapshot.environment, snapshot.revision, snapshot.etag
)
UPDATE honua.metadata_v2_current AS current
SET etag = updated.etag
FROM updated_snapshots AS updated
WHERE (current.environment, current.revision) = (updated.environment, updated.revision);
