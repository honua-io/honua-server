-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- honua:migration-phase contract
-- honua:compatibility-review reason=Rename the persisted service-layer option while preserving every snapshot and option value.

DO $$
DECLARE
    snapshots_before bigint;
    snapshots_after bigint;
    current_rows_before bigint;
    current_rows_after bigint;
    old_bindings_before bigint;
    neutral_bindings_before bigint;
    neutral_bindings_after bigint;
    expected_digest text;
    actual_digest text;
BEGIN
    SELECT count(*) INTO snapshots_before
    FROM $HonuaSchema$.metadata_v2_snapshots;

    SELECT count(*) INTO current_rows_before
    FROM $HonuaSchema$.metadata_v2_current;

    IF EXISTS (
        SELECT 1
        FROM $HonuaSchema$.metadata_v2_snapshots AS snapshot
        CROSS JOIN LATERAL jsonb_array_elements(
            CASE WHEN jsonb_typeof(snapshot.document->'storageBindings') = 'array'
                 THEN snapshot.document->'storageBindings' ELSE '[]'::jsonb END
        ) AS binding(value)
        WHERE jsonb_typeof(binding.value->'options') = 'object'
          AND binding.value->'options' ? 'arcgisLayerId'
          AND binding.value->'options' ? 'layerId'
    ) THEN
        RAISE EXCEPTION 'Metadata binding key cutover found conflicting old and new keys; no rows changed';
    END IF;

    CREATE TEMP TABLE metadata_binding_key_cutover ON COMMIT DROP AS
    SELECT snapshot.environment,
           snapshot.revision,
           snapshot.document AS original_document,
           jsonb_set(
               snapshot.document,
               '{storageBindings}',
               (
                   SELECT jsonb_agg(
                       CASE
                           WHEN jsonb_typeof(binding.value->'options') = 'object'
                            AND binding.value->'options' ? 'arcgisLayerId'
                           THEN jsonb_set(
                               binding.value,
                               '{options}',
                               (binding.value->'options' - 'arcgisLayerId')
                                   || jsonb_build_object('layerId', binding.value->'options'->'arcgisLayerId'),
                               true
                           )
                           ELSE binding.value
                       END
                       ORDER BY binding.ordinality
                   )
                   FROM jsonb_array_elements(
                       CASE WHEN jsonb_typeof(snapshot.document->'storageBindings') = 'array'
                            THEN snapshot.document->'storageBindings' ELSE '[]'::jsonb END
                   ) WITH ORDINALITY AS binding(value, ordinality)
               ),
               true
           ) AS expected_document
    FROM $HonuaSchema$.metadata_v2_snapshots AS snapshot
    WHERE jsonb_typeof(snapshot.document->'storageBindings') = 'array'
      AND EXISTS (
          SELECT 1
          FROM jsonb_array_elements(
              CASE WHEN jsonb_typeof(snapshot.document->'storageBindings') = 'array'
                   THEN snapshot.document->'storageBindings' ELSE '[]'::jsonb END
          ) AS binding(value)
          WHERE jsonb_typeof(binding.value->'options') = 'object'
            AND binding.value->'options' ? 'arcgisLayerId'
      );

    SELECT count(*) FILTER (WHERE jsonb_typeof(binding.value->'options') = 'object'
                              AND binding.value->'options' ? 'arcgisLayerId'),
           count(*) FILTER (WHERE jsonb_typeof(binding.value->'options') = 'object'
                              AND binding.value->'options' ? 'layerId')
    INTO old_bindings_before, neutral_bindings_before
    FROM metadata_binding_key_cutover AS staged
    CROSS JOIN LATERAL jsonb_array_elements(staged.original_document->'storageBindings') AS binding(value);

    SELECT count(*) INTO neutral_bindings_after
    FROM metadata_binding_key_cutover AS staged
    CROSS JOIN LATERAL jsonb_array_elements(staged.expected_document->'storageBindings') AS binding(value)
    WHERE jsonb_typeof(binding.value->'options') = 'object'
      AND binding.value->'options' ? 'layerId';

    SELECT md5(coalesce(string_agg(
               concat_ws(E'\x1f', environment, revision::text, expected_document::text),
               E'\n' ORDER BY environment, revision), ''))
    INTO expected_digest
    FROM metadata_binding_key_cutover;

    UPDATE $HonuaSchema$.metadata_v2_snapshots AS snapshot
    SET document = staged.expected_document,
        etag = '"' || lower(encode(sha256(convert_to(staged.expected_document::text, 'UTF8')), 'hex')) || '"'
    FROM metadata_binding_key_cutover AS staged
    WHERE snapshot.environment = staged.environment
      AND snapshot.revision = staged.revision;

    UPDATE $HonuaSchema$.metadata_v2_current AS active_row
    SET etag = snapshot.etag
    FROM $HonuaSchema$.metadata_v2_snapshots AS snapshot
    INNER JOIN metadata_binding_key_cutover AS staged
      ON snapshot.environment = staged.environment
     AND snapshot.revision = staged.revision
    WHERE active_row.environment = snapshot.environment
      AND active_row.revision = snapshot.revision;

    SELECT count(*) INTO snapshots_after
    FROM $HonuaSchema$.metadata_v2_snapshots;

    SELECT count(*) INTO current_rows_after
    FROM $HonuaSchema$.metadata_v2_current;

    SELECT count(*) INTO neutral_bindings_after
    FROM $HonuaSchema$.metadata_v2_snapshots AS snapshot
    INNER JOIN metadata_binding_key_cutover AS staged
      ON snapshot.environment = staged.environment
     AND snapshot.revision = staged.revision
    CROSS JOIN LATERAL jsonb_array_elements(snapshot.document->'storageBindings') AS binding(value)
    WHERE jsonb_typeof(binding.value->'options') = 'object'
      AND binding.value->'options' ? 'layerId';

    SELECT md5(coalesce(string_agg(
               concat_ws(E'\x1f', snapshot.environment, snapshot.revision::text, snapshot.document::text),
               E'\n' ORDER BY snapshot.environment, snapshot.revision), ''))
    INTO actual_digest
    FROM $HonuaSchema$.metadata_v2_snapshots AS snapshot
    INNER JOIN metadata_binding_key_cutover AS staged
      ON snapshot.environment = staged.environment
     AND snapshot.revision = staged.revision;

    IF snapshots_before <> snapshots_after
       OR current_rows_before <> current_rows_after
       OR neutral_bindings_after <> old_bindings_before + neutral_bindings_before
       OR actual_digest IS DISTINCT FROM expected_digest
       OR EXISTS (
           SELECT 1
           FROM $HonuaSchema$.metadata_v2_snapshots AS snapshot
           CROSS JOIN LATERAL jsonb_array_elements(
               CASE WHEN jsonb_typeof(snapshot.document->'storageBindings') = 'array'
                    THEN snapshot.document->'storageBindings' ELSE '[]'::jsonb END
           ) AS binding(value)
           WHERE jsonb_typeof(binding.value->'options') = 'object'
             AND binding.value->'options' ? 'arcgisLayerId'
       )
       OR EXISTS (
           SELECT 1
           FROM metadata_binding_key_cutover AS staged
           INNER JOIN $HonuaSchema$.metadata_v2_current AS active_row
             ON active_row.environment = staged.environment
            AND active_row.revision = staged.revision
           INNER JOIN $HonuaSchema$.metadata_v2_snapshots AS snapshot
             ON snapshot.environment = active_row.environment
            AND snapshot.revision = active_row.revision
           WHERE snapshot.etag IS DISTINCT FROM active_row.etag
       ) THEN
        RAISE EXCEPTION 'Metadata binding key cutover validation failed; transaction rollback preserves all snapshot data';
    END IF;
END
$$;
