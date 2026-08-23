FROM maven:3.9.11-eclipse-temurin-17@sha256:e4a7ace3dc0d645ed97f8d9ad0b0d3f0b14fa8d150138f27f116d7105a639b82 AS build

ARG ETS_COMMIT=75abd1f37fc3aad95163fdce2e33e393b1ba5a88

# The pinned upstream POM excludes testdata from the AIO jar even though Jobs.java
# loads this exact upstream GeoTIFF from the classpath. Add only that pinned source
# resource; no suite assertion or executable class is modified.
RUN git clone --filter=blob:none https://github.com/opengeospatial/ets-ogcapi-processes10.git /src/ets \
    && cd /src/ets \
    && git checkout --detach "${ETS_COMMIT}" \
    && test "$(git rev-parse HEAD)" = "${ETS_COMMIT}" \
    && mvn --batch-mode --no-transfer-progress -DskipTests package \
    && cp "$(find target -maxdepth 1 -type f -name '*-aio.jar' -print -quit)" /tmp/ets-aio.jar \
    && jar --update --file /tmp/ets-aio.jar \
        -C src/main/resources org/opengis/cite/testdata/testgeotiff.tiff \
    && sha256sum src/main/resources/org/opengis/cite/testdata/testgeotiff.tiff \
        | cut -d ' ' -f 1 > /tmp/ets-testdata.sha256

FROM eclipse-temurin:17-jre@sha256:92999aea37688157a53a40bfcb187c30f317422e028045fd5fc5c548fde9e626

ARG ETS_COMMIT=75abd1f37fc3aad95163fdce2e33e393b1ba5a88

RUN useradd --create-home --uid 10001 --shell /usr/sbin/nologin cite
COPY --from=build /tmp/ets-aio.jar /opt/ets/ets-ogcapi-processes10-aio.jar
COPY --from=build /tmp/ets-testdata.sha256 /opt/ets/testdata.sha256
RUN { \
        printf 'suite=ets-ogcapi-processes10\nversion=1.4-SNAPSHOT\ncommit=%s\nsource=https://github.com/opengeospatial/ets-ogcapi-processes10\n' \
            "${ETS_COMMIT}"; \
        printf 'packaging=upstream-aio-plus-pinned-testdata\ntestdata_sha256='; \
        cat /opt/ets/testdata.sha256; \
    } > /opt/ets/PROVENANCE \
    && chown -R cite:cite /opt/ets

USER cite
WORKDIR /home/cite
ENTRYPOINT ["java", "-jar", "/opt/ets/ets-ogcapi-processes10-aio.jar"]
