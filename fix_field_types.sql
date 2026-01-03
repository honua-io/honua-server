-- Fix field types to match FieldType enum values
UPDATE honua.layer_fields
SET field_type = 'Integer'
WHERE layer_id = 1000 AND field_type = 'integer';

UPDATE honua.layer_fields
SET field_type = 'String'
WHERE layer_id = 1000 AND field_type = 'text';