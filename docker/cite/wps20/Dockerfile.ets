FROM maven:3.9.11-eclipse-temurin-17 AS build

ARG ETS_COMMIT=e2acc691440fad98d32e873a6b7237c9d759b8df

RUN git clone --filter=blob:none https://github.com/opengeospatial/ets-wps20.git /src/ets-wps20 \
    && cd /src/ets-wps20 \
    && git checkout --detach "${ETS_COMMIT}" \
    && test "$(git rev-parse HEAD)" = "${ETS_COMMIT}" \
    && mvn --batch-mode --no-transfer-progress -DskipTests package \
    && cp "$(find target -maxdepth 1 -type f -name '*-aio.jar' -print -quit)" /tmp/ets-wps20-aio.jar

FROM eclipse-temurin:17-jre

ARG ETS_COMMIT=e2acc691440fad98d32e873a6b7237c9d759b8df

RUN useradd --create-home --uid 10001 --shell /usr/sbin/nologin cite
COPY --from=build /tmp/ets-wps20-aio.jar /opt/ets/ets-wps20-aio.jar
RUN printf 'suite=ets-wps20\nversion=1.1\ncommit=%s\nsource=https://github.com/opengeospatial/ets-wps20\n' \
        "${ETS_COMMIT}" > /opt/ets/PROVENANCE \
    && chown -R cite:cite /opt/ets

USER cite
WORKDIR /home/cite
ENTRYPOINT ["java", "-jar", "/opt/ets/ets-wps20-aio.jar"]
