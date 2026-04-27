# PyQGIS client interop lane. Uses the upstream qgis image so the rendering
# stack matches the desktop client. Headless via QT_QPA_PLATFORM=offscreen.

FROM qgis/qgis:ltr

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    QT_QPA_PLATFORM=offscreen \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_BREAK_SYSTEM_PACKAGES=1

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        python3-pip \
        python3-venv \
        ca-certificates \
        curl \
        jq \
        xvfb \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /workspace

# Install Python test deps to system python so the QGIS-bundled python can
# import them. PIP_BREAK_SYSTEM_PACKAGES is required because the qgis image
# uses Debian's python3 as its system interpreter.
COPY tests/python/requirements.txt /tmp/requirements.txt
RUN pip3 install -r /tmp/requirements.txt

COPY docker/client-compat/pyqgis/run.sh /usr/local/bin/run-pyqgis-lane
RUN chmod +x /usr/local/bin/run-pyqgis-lane

ENTRYPOINT ["/usr/local/bin/run-pyqgis-lane"]
