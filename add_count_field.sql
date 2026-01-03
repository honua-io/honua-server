-- Add count field to layer fields
INSERT INTO honua.layer_fields (layer_id, field_name, field_type, field_order, nullable, description)
VALUES (1000, 'count', 'Integer', 6, true, 'Count field for testing')
ON CONFLICT (layer_id, field_name) DO NOTHING;

-- Update existing features to include count values in the range the tests expect
UPDATE features
SET attributes = jsonb_set(attributes, '{count}',
    CASE
        WHEN (attributes->>'value')::bigint >= 1000000 THEN '50'::jsonb
        WHEN (attributes->>'value')::bigint >= 500000 THEN '25'
        ELSE '10'
    END)
WHERE layer_id = 1000;