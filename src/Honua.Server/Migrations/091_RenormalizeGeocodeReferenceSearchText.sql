-- Re-normalize local geocoder reference search_text after the separator-canonicalization
-- change: commas/semicolons are now replaced with spaces on both the load and query paths
-- (GeocodeReferenceText.Normalize), so rows loaded under the previous comma-preserving rule
-- must be re-normalized once or prefix matches that cross a separator stop working.
-- Guarded: the table is created lazily by the import endpoint (or manually by operators) and
-- may not exist yet. Custom schema/table configurations are covered by the documented manual
-- UPDATE in docs/reference/geocoding/local-postgis-geocoder.md.
DO $$
BEGIN
    IF to_regclass('public.honua_geocode_reference') IS NOT NULL THEN
        UPDATE public.honua_geocode_reference
        SET search_text = lower(regexp_replace(trim(regexp_replace(search_text, '[,;]', ' ', 'g')), '\s+', ' ', 'g'))
        WHERE search_text ~ '[,;]';
    END IF;
END $$;
