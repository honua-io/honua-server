-- Creates pgRouting in the fixture database, for the NAServer certification
-- cells. Runs from the postgis image's docker-entrypoint-initdb.d hook, before
-- the honua server starts, because the server owns schema creation and its
-- migration journal but not extensions.
--
-- The 30- prefix is load-bearing: these scripts run in filename order, and
-- pgRouting requires postgis, which the base image installs from its own
-- 10_postgis.sh. A 10- prefix sorts before that ('-' < '_') and fails with
-- 'required extension "postgis" is not installed'. CASCADE is belt and braces.
CREATE EXTENSION IF NOT EXISTS pgrouting CASCADE;
