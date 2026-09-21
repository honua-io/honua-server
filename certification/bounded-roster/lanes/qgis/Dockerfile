# QGIS desktop client lane (desktop-qgis): the official QGIS 3.44.13 image, driven
# headless through PyQGIS. run-roster.sh records the resolved image digest.
FROM qgis/qgis:3.44.13@sha256:59e160b2ea3f881be039ed49936c42e75e8f336688951f22147676db743f4e47
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 QT_QPA_PLATFORM=offscreen
WORKDIR /roster
