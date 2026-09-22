# postgis/postgis:16-3.4 plus pgRouting, for the NAServer certification cells.
#
# The base image is Debian bullseye with the PostgreSQL APT repository already
# configured, so postgresql-16-pgrouting resolves without adding a source. The
# extension itself is created by initdb/10-pgrouting.sql; installing the package
# alone would leave CREATE EXTENSION to fail at query time.
FROM postgis/postgis:16-3.4

RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-16-pgrouting \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*
