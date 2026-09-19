# Python client lane: OWSLib (py-owslib) and PySTAC-Client (py-pystac).
FROM python:3.12-slim-bookworm@sha256:782412e85d0f0984994c290652577d4018aff08145c85b262bb63dc0c7522254
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 PIP_DISABLE_PIP_VERSION_CHECK=1 PIP_NO_CACHE_DIR=1
COPY lanes/python/requirements.txt /tmp/requirements.txt
RUN pip install -r /tmp/requirements.txt && pip freeze > /opt/lane-freeze.txt
WORKDIR /roster
ENV PYTHONPATH=/roster/lib
