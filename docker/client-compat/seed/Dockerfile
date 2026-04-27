# Seed runner — applies the canonical client-compat SQL bootstrap and the
# browser-compat YAML seed against postgres before honua starts. This service
# exits 0 on success so honua can wait via service_completed_successfully.

FROM python:3.12-slim

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-client \
 && pip install --no-cache-dir pyyaml \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

COPY docker/client-compat/seed/run.sh /usr/local/bin/run-seed
RUN chmod +x /usr/local/bin/run-seed

ENTRYPOINT ["/usr/local/bin/run-seed"]
