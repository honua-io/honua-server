-- Add status field to layer fields
INSERT INTO honua.layer_fields (layer_id, field_name, field_type, field_order, nullable, description)
VALUES (1000, 'status', 'String', 7, true, 'Status field for testing')
ON CONFLICT (layer_id, field_name) DO NOTHING;

-- Update existing features to include status values
UPDATE features
SET attributes = jsonb_set(attributes, '{status}',
    CASE
        WHEN (attributes->>'value')::bigint >= 1000000 THEN '"active"'::jsonb
        WHEN (attributes->>'value')::bigint >= 500000 THEN '"pending"'::jsonb
        ELSE '"inactive"'::jsonb
    END)
WHERE layer_id = 1000;