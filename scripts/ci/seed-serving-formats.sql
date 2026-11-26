DO $serving_formats$
DECLARE
    active_snapshot record;
    snapshot_document jsonb;
    feature_service jsonb;
    map_service jsonb;
    ogc_service jsonb;
    feature_publication jsonb;
    publication_type text;
    publication_id text;
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
        map_service := feature_service || jsonb_build_object(
                'metadata', jsonb_build_object('id', 'svc-cng-map', 'name', 'cng-map'),
                'serviceType', 'esri-map-service',
                'publicationIds', jsonb_build_array('pub-cng-map-1000', 'pub-cng-wms-1000'),
                'protocols', jsonb_build_array('MapServer', 'Wms'),
                'enabledProtocols', jsonb_build_array('MapServer', 'Wms'));
        ogc_service := feature_service || jsonb_build_object(
                'metadata', jsonb_build_object('id', 'svc-cng-ogc', 'name', 'cng-ogc', 'title', 'CNG OGC and OData'),
                'serviceType', 'ogc-api-features',
                'publicationIds', jsonb_build_array('pub-cng-ogc-1000', 'pub-cng-odata-1000'),
                'protocols', jsonb_build_array('OgcFeatures', 'OGC-API-Tiles', 'OData'),
                'enabledProtocols', jsonb_build_array('OgcFeatures', 'OGC-API-Tiles', 'OData'));
        feature_publication := jsonb_path_query_first(snapshot_document,
            '$.publications[*] ? (@.metadata.id == "pub-cng-feature-1000")');
        snapshot_document := jsonb_set(snapshot_document, '{services}',
            snapshot_document->'services' || jsonb_build_array(map_service, ogc_service));
        FOREACH publication_type IN ARRAY ARRAY['esri-map-layer', 'wms-layer']
        LOOP
            publication_id := CASE publication_type WHEN 'esri-map-layer'
                THEN 'pub-cng-map-1000' ELSE 'pub-cng-wms-1000' END;
            snapshot_document := jsonb_set(snapshot_document, '{publications}',
                snapshot_document->'publications' || jsonb_build_array(
                    feature_publication || jsonb_build_object(
                        'metadata', jsonb_build_object('id', publication_id, 'name', '1000'),
                        'serviceId', 'svc-cng-map',
                        'publicationType', publication_type,
                        'supportedFormats', jsonb_build_array('png'),
                        'capabilities', jsonb_build_array('Map', 'Query'))));
        END LOOP;
        snapshot_document := jsonb_set(snapshot_document, '{publications}',
            snapshot_document->'publications' || jsonb_build_array(
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
$serving_formats$;
