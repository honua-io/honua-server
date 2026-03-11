-- OGC CITE WFS 2.0 Test Data Initialization
-- Creates basic test datasets for WFS 2.0 conformance testing

-- Enable PostGIS if not already enabled
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_topology;

-- Create CITE test schema
CREATE SCHEMA IF NOT EXISTS cite_test;
SET search_path = cite_test, public;

-- Test feature type 1: Simple points of interest
CREATE TABLE IF NOT EXISTS cite_test.poi (
    id SERIAL PRIMARY KEY,
    fid VARCHAR(50) UNIQUE NOT NULL,
    name VARCHAR(100) NOT NULL,
    category VARCHAR(50),
    description TEXT,
    geom GEOMETRY(POINT, 4326),
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Test feature type 2: Administrative boundaries
CREATE TABLE IF NOT EXISTS cite_test.admin_boundaries (
    id SERIAL PRIMARY KEY,
    fid VARCHAR(50) UNIQUE NOT NULL,
    name VARCHAR(100) NOT NULL,
    admin_level INTEGER,
    area_km2 NUMERIC(10,2),
    population INTEGER,
    geom GEOMETRY(POLYGON, 4326),
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Test feature type 3: Transportation lines
CREATE TABLE IF NOT EXISTS cite_test.transport_lines (
    id SERIAL PRIMARY KEY,
    fid VARCHAR(50) UNIQUE NOT NULL,
    name VARCHAR(100),
    transport_type VARCHAR(50),
    length_km NUMERIC(8,2),
    geom GEOMETRY(LINESTRING, 4326),
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Insert sample POI data
INSERT INTO cite_test.poi (fid, name, category, description, geom) VALUES
    ('poi.1', 'City Hall', 'government', 'Municipal government building', ST_GeomFromText('POINT(-122.4194 37.7749)', 4326)),
    ('poi.2', 'Central Library', 'public', 'Main public library branch', ST_GeomFromText('POINT(-122.4158 37.7803)', 4326)),
    ('poi.3', 'Fire Station 1', 'emergency', 'Primary fire station downtown', ST_GeomFromText('POINT(-122.4089 37.7849)', 4326)),
    ('poi.4', 'Hospital', 'medical', 'General hospital with emergency services', ST_GeomFromText('POINT(-122.4064 37.7749)', 4326)),
    ('poi.5', 'School', 'education', 'Elementary school', ST_GeomFromText('POINT(-122.4194 37.7699)', 4326))
ON CONFLICT (fid) DO NOTHING;

-- Insert sample administrative boundary data
INSERT INTO cite_test.admin_boundaries (fid, name, admin_level, area_km2, population, geom) VALUES
    ('admin.1', 'Downtown District', 1, 5.2, 15000,
     ST_GeomFromText('POLYGON((-122.43 37.77, -122.41 37.77, -122.41 37.79, -122.43 37.79, -122.43 37.77))', 4326)),
    ('admin.2', 'Residential Zone A', 2, 12.8, 45000,
     ST_GeomFromText('POLYGON((-122.45 37.75, -122.42 37.75, -122.42 37.78, -122.45 37.78, -122.45 37.75))', 4326))
ON CONFLICT (fid) DO NOTHING;

-- Insert sample transportation data
INSERT INTO cite_test.transport_lines (fid, name, transport_type, length_km, geom) VALUES
    ('transport.1', 'Main Street', 'road', 2.5,
     ST_GeomFromText('LINESTRING(-122.43 37.77, -122.42 37.775, -122.41 37.78)', 4326)),
    ('transport.2', 'Metro Line Blue', 'rail', 8.2,
     ST_GeomFromText('LINESTRING(-122.44 37.76, -122.43 37.77, -122.42 37.78, -122.41 37.79)', 4326))
ON CONFLICT (fid) DO NOTHING;

-- Create spatial indexes for performance
CREATE INDEX IF NOT EXISTS poi_geom_idx ON cite_test.poi USING GIST (geom);
CREATE INDEX IF NOT EXISTS admin_boundaries_geom_idx ON cite_test.admin_boundaries USING GIST (geom);
CREATE INDEX IF NOT EXISTS transport_lines_geom_idx ON cite_test.transport_lines USING GIST (geom);

-- Create regular indexes on commonly queried fields
CREATE INDEX IF NOT EXISTS poi_category_idx ON cite_test.poi (category);
CREATE INDEX IF NOT EXISTS admin_boundaries_admin_level_idx ON cite_test.admin_boundaries (admin_level);
CREATE INDEX IF NOT EXISTS transport_lines_type_idx ON cite_test.transport_lines (transport_type);

-- Grant permissions for the application user
GRANT USAGE ON SCHEMA cite_test TO postgres;
GRANT SELECT ON ALL TABLES IN SCHEMA cite_test TO postgres;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA cite_test TO postgres;

-- Set default privileges for future tables
ALTER DEFAULT PRIVILEGES IN SCHEMA cite_test GRANT SELECT ON TABLES TO postgres;

-- Add some summary info for debugging
DO $$
BEGIN
    RAISE NOTICE 'CITE test data initialization completed';
    RAISE NOTICE 'POI records: %', (SELECT COUNT(*) FROM cite_test.poi);
    RAISE NOTICE 'Admin boundary records: %', (SELECT COUNT(*) FROM cite_test.admin_boundaries);
    RAISE NOTICE 'Transport line records: %', (SELECT COUNT(*) FROM cite_test.transport_lines);
END $$;

-- Create view for all feature types (useful for generic queries)
CREATE OR REPLACE VIEW cite_test.all_features AS
SELECT
    'poi' as feature_type,
    fid,
    name,
    category as type_detail,
    description,
    geom,
    created_date
FROM cite_test.poi
UNION ALL
SELECT
    'admin_boundary' as feature_type,
    fid,
    name,
    admin_level::text as type_detail,
    CONCAT('Area: ', area_km2, ' km², Population: ', population) as description,
    geom,
    created_date
FROM cite_test.admin_boundaries
UNION ALL
SELECT
    'transport_line' as feature_type,
    fid,
    COALESCE(name, 'Unnamed') as name,
    transport_type as type_detail,
    CONCAT('Length: ', length_km, ' km') as description,
    geom,
    created_date
FROM cite_test.transport_lines;

GRANT SELECT ON cite_test.all_features TO postgres;