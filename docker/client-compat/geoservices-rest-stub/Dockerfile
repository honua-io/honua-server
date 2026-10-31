# geoservices-rest-stub REST lane.
#
# REST-only client-compat lane. It is not a desktop client. Render cases stay
# skip. Desktop evidence is identifier desktop-gis in honua-esri-compat.

FROM python:3.12-slim@sha256:78387bc3881b8273120a12ebe6c1ab22b018ccc2c9adf565ae1ac9b536e184ea

ENV PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl jq ca-certificates \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

RUN pip install httpx==0.27.0

COPY docker/client-compat/geoservices-rest-stub/stub_runner.py /usr/local/bin/stub_runner.py
COPY docker/client-compat/geoservices-rest-stub/run.sh /usr/local/bin/run-geoservices-rest-stub-lane
RUN chmod +x /usr/local/bin/run-geoservices-rest-stub-lane /usr/local/bin/stub_runner.py

ENTRYPOINT ["/usr/local/bin/run-geoservices-rest-stub-lane"]
