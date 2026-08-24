# DuckDB Spatial client interop lane (honua-server#3392).
#
# DuckDB is the analytical-SQL canonical client: the suite drives the server
# through `ST_Read('OAPIF:...')` over GDAL's OGC API - Features driver,
# `read_json_auto(...)` over httpfs, and ordinary DuckDB SQL on top.
#
# Compose must provide these mounts (see docker/client-compat/compose.yml):
#   - ../../tests:/workspace/tests:ro   -> the suite + shared/ helpers + seed
#   - ./output/duckdb:/output           -> where the .cert.json envelope lands
FROM python:3.12-slim-bookworm

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1 \
    # DuckDB resolves extensions from this directory. Setting it before the
    # build-time INSTALL bakes the extension binaries into the image layer and
    # makes LOAD work with no egress at run time.
    DUCKDB_EXTENSION_DIRECTORY=/opt/duckdb-extensions

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

COPY docker/client-compat/duckdb/requirements.txt /tmp/requirements.txt
RUN pip3 install --no-cache-dir -r /tmp/requirements.txt

# Pre-install the spatial + httpfs extensions at BUILD time. They are
# downloaded from extensions.duckdb.org on first INSTALL; doing it here means
# the lane never depends on egress while a certification run is in flight (and
# never silently certifies a different extension build than the one this image
# was tested against). The second step fails the build if either extension
# cannot be LOADed back out of the baked directory.
RUN mkdir -p "${DUCKDB_EXTENSION_DIRECTORY}" \
 && python -c "import duckdb; duckdb.sql('INSTALL spatial; INSTALL httpfs;')" \
 && python -c "import duckdb; con = duckdb.connect(); con.execute('LOAD spatial; LOAD httpfs;'); rows = con.execute(\"SELECT extension_name, extension_version FROM duckdb_extensions() WHERE loaded\").fetchall(); names = {name for name, _ in rows}; assert {'spatial', 'httpfs'} <= names, rows; print('duckdb', duckdb.__version__, dict(rows))"

COPY docker/client-compat/duckdb/run.sh /usr/local/bin/run-duckdb-lane
RUN chmod +x /usr/local/bin/run-duckdb-lane

ENTRYPOINT ["/usr/local/bin/run-duckdb-lane"]
