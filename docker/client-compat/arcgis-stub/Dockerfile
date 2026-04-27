# ArcGIS Pro REST stub lane.
#
# ArcGIS Pro itself runs only on Windows + a paid license. This lane exercises
# the same FeatureServer / MapServer REST endpoints the desktop client
# consumes (capabilities, layers, query, identify, export) and emits a
# comparable cert envelope. Full Pro coverage requires a licensed runner —
# that gap is tracked in docs/gis/gap-report.md until provisioned.

FROM python:3.12-slim

ENV PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl jq ca-certificates \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

RUN pip install httpx==0.27.0

COPY docker/client-compat/arcgis-stub/stub_runner.py /usr/local/bin/stub_runner.py
COPY docker/client-compat/arcgis-stub/run.sh /usr/local/bin/run-arcgis-stub-lane
RUN chmod +x /usr/local/bin/run-arcgis-stub-lane /usr/local/bin/stub_runner.py

ENTRYPOINT ["/usr/local/bin/run-arcgis-stub-lane"]
