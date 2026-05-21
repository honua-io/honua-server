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
#
# --ignore-installed is required because the qgis/qgis:ltr base image ships
# transitive Python dependencies (e.g. rpds-py, jsonschema-specifications,
# referencing) as Debian dist-packages without RECORD files. When a newer
# requirement (jsonschema>=4.25.0 pulling rpds-py>=0.25.0) tries to upgrade
# them, pip aborts with "Cannot uninstall ...: no RECORD file was found"
# and the entire pyqgis lane Docker build fails before tests can run.
# Overlaying these packages in /usr/local/lib instead of touching the
# Debian-managed copies sidesteps the conflict while still keeping the
# QGIS-bundled python able to import them.
COPY tests/python/requirements.txt /tmp/requirements.txt
RUN pip3 install --ignore-installed -r /tmp/requirements.txt

COPY docker/client-compat/pyqgis/run.sh /usr/local/bin/run-pyqgis-lane
RUN chmod +x /usr/local/bin/run-pyqgis-lane

ENTRYPOINT ["/usr/local/bin/run-pyqgis-lane"]
