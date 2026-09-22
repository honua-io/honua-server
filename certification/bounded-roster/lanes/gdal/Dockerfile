# GDAL/OGR client lane (lanes gdal, gdal-cog, gdal-flatgeobuf). The governed GDAL
# release is the official OSGeo image tag passed as GDAL_IMAGE; run-roster.sh
# records the resolved image digest in the lane identity.
ARG GDAL_IMAGE
FROM ${GDAL_IMAGE}
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
RUN python3 -c "from osgeo import gdal, ogr; print(gdal.VersionInfo('--version'))"
WORKDIR /roster
