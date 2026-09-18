# Seed runner — applies the canonical client-compat SQL bootstrap and the
# browser-compat YAML seed against postgres after honua completes its migrations. This service
# exits 0 on success so client lanes wait via service_completed_successfully.

FROM python:3.12-slim@sha256:78387bc3881b8273120a12ebe6c1ab22b018ccc2c9adf565ae1ac9b536e184ea

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-client \
 && pip install --no-cache-dir pyyaml \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

COPY docker/client-compat/seed/run.sh /usr/local/bin/run-seed
# Publishes the PMTiles archive through the running server at the end of the
# seed. Stdlib only - this image has no curl, which is how the first attempt
# failed with 127.
COPY docker/client-compat/seed/publish-pmtiles.py /usr/local/bin/publish-pmtiles.py
# Generates the 3D Tiles / I3S scene from the polygon fixture the same way: a
# scene is a derived artifact only the running server can produce.
COPY docker/client-compat/seed/publish-scene.py /usr/local/bin/publish-scene.py
RUN chmod +x /usr/local/bin/run-seed /usr/local/bin/publish-pmtiles.py /usr/local/bin/publish-scene.py

ENTRYPOINT ["/usr/local/bin/run-seed"]
