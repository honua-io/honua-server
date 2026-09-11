FROM maven:3.9.11-eclipse-temurin-17@sha256:e4a7ace3dc0d645ed97f8d9ad0b0d3f0b14fa8d150138f27f116d7105a639b82 AS build

ARG ETS_COMMIT=e2acc691440fad98d32e873a6b7237c9d759b8df

RUN git clone --filter=blob:none https://github.com/opengeospatial/ets-wps20.git /src/ets-wps20 \
    && cd /src/ets-wps20 \
    && git checkout --detach "${ETS_COMMIT}" \
    && test "$(git rev-parse HEAD)" = "${ETS_COMMIT}" \
    && mvn --batch-mode --no-transfer-progress -DskipTests package \
    && cp "$(find target -maxdepth 1 -type f -name '*-aio.jar' -print -quit)" /tmp/ets-wps20-aio.jar

FROM eclipse-temurin:17-jre@sha256:13cc28a6cc72a38ce1f00c906be3580c1a3e604b8984d694f369a96742abc93b

ARG ETS_COMMIT=e2acc691440fad98d32e873a6b7237c9d759b8df

RUN useradd --create-home --uid 10001 --shell /usr/sbin/nologin cite
COPY --from=build /tmp/ets-wps20-aio.jar /opt/ets/ets-wps20-aio.jar
RUN printf 'suite=ets-wps20\nversion=1.1\ncommit=%s\nsource=https://github.com/opengeospatial/ets-wps20\nteam_engine=not-applicable\n' \
        "${ETS_COMMIT}" > /opt/ets/PROVENANCE \
    && chown -R cite:cite /opt/ets

USER cite
WORKDIR /home/cite
ENTRYPOINT ["java", "-jar", "/opt/ets/ets-wps20-aio.jar"]
