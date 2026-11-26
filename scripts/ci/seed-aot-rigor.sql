-- Extend the seeded CNG Metadata v2 snapshot with OGC API Features, OGC API Tiles,
-- and OData publications for layer 1000 so NativeAOT parity and soak suites exercise
-- weak ETag 304, MVT tiles, and OData conditional GET against real PostGIS rows.
DO $aot_rigor$
DECLARE
    active_snapshot record;
    snapshot_document jsonb;
    feature_service jsonb;
    feature_publication jsonb;
    ogc_service jsonb;
    snapshot_etag text;
    target_revision bigint;
BEGIN
    FOR active_snapshot IN
        SELECT snapshots.*
          FROM honua.metadata_v2_snapshots snapshots
          JOIN honua.metadata_v2_current current_snapshot
            ON current_snapshot.environment = snapshots.environment
           AND current_snapshot.revision = snapshots.revision
         WHERE snapshots.environment IN ('default', 'Test')
    LOOP
        snapshot_document := active_snapshot.document;
        target_revision := active_snapshot.revision + 1;
        feature_service := jsonb_path_query_first(snapshot_document,
            '$.services[*] ? (@.metadata.id == "svc-cng-feature")');
        feature_publication := jsonb_path_query_first(snapshot_document,
            '$.publications[*] ? (@.metadata.id == "pub-cng-feature-1000")');
        ogc_service := feature_service || jsonb_build_object(
            'metadata', jsonb_build_object('id', 'svc-cng-ogc', 'name', 'cng-ogc', 'title', 'CNG OGC and OData'),
            'serviceType', 'ogc-api-features',
            'publicationIds', jsonb_build_array('pub-cng-ogc-1000', 'pub-cng-odata-1000'),
            'protocols', jsonb_build_array('OgcFeatures', 'OGC-API-Tiles', 'OData'),
            'enabledProtocols', jsonb_build_array('OgcFeatures', 'OGC-API-Tiles', 'OData'));
        snapshot_document := jsonb_set(snapshot_document, '{services}',
            snapshot_document->'services' || jsonb_build_array(ogc_service));
        snapshot_document := jsonb_set(snapshot_document, '{publications}',
            snapshot_document->'publications'
            || jsonb_build_array(
                feature_publication || jsonb_build_object(
                    'metadata', jsonb_build_object('id', 'pub-cng-ogc-1000', 'name', '1000'),
                    'serviceId', 'svc-cng-ogc',
                    'publicationType', 'ogc-collection',
                    'supportedFormats', jsonb_build_array('json', 'geojson', 'mvt'),
                    'capabilities', jsonb_build_array('Query', 'Tile')),
                feature_publication || jsonb_build_object(
                    'metadata', jsonb_build_object('id', 'pub-cng-odata-1000', 'name', '1000'),
                    'serviceId', 'svc-cng-ogc',
                    'publicationType', 'odata-entity-set',
                    'supportedFormats', jsonb_build_array('json'),
                    'capabilities', jsonb_build_array('Query'))));
        snapshot_document := snapshot_document || jsonb_build_object(
            'revision', target_revision, 'generatedAt', NOW());
        snapshot_etag := '"' || md5(snapshot_document::text) || '"';
        INSERT INTO honua.metadata_v2_snapshots (
            environment, revision, schema_version, api_version, document, etag, generated_at)
        VALUES (active_snapshot.environment, target_revision, active_snapshot.schema_version,
            active_snapshot.api_version, snapshot_document, snapshot_etag, NOW());
        UPDATE honua.metadata_v2_current
           SET revision = target_revision, etag = snapshot_etag, activated_at = NOW()
         WHERE environment = active_snapshot.environment;
    END LOOP;
END
$aot_rigor$;
