# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
#
# Database image for the Compose installs: PostgreSQL 17 with PostGIS 3.5 and pgRouting 3.7 on a
# maintained Debian base. The extension versions are Debian's, the same ones the previous base
# shipped, so existing databases need no extension update. The privilege-drop helper the official
# entrypoint uses is rebuilt from pinned source with the pinned Go toolchain, and the weekly
# rebuild in .github/workflows/install-images.yml takes Debian security updates.
ARG GO_IMAGE=golang:1.27.4-alpine3.23@sha256:f70850ef6ce341e8b6427f677049145d63c0b7ce002d4d7e3d464828b1b96dcd
ARG POSTGRES_IMAGE=postgres:17-trixie@sha256:b60e90042aa6b8dbecd58e224b3e36114dbf8ad754465264c2476ad58a86e45c

FROM --platform=$BUILDPLATFORM ${GO_IMAGE} AS gosu
ARG TARGETOS
ARG TARGETARCH
# gosu 1.20
ARG GOSU_COMMIT=29b939aa5bd4dc588af765b8248f6e39150d2565
RUN set -eux; \
    apk add --no-cache git; \
    git init -q /src; \
    git -C /src fetch -q --depth 1 https://github.com/tianon/gosu "$GOSU_COMMIT"; \
    git -C /src checkout -q FETCH_HEAD; \
    cd /src; \
    CGO_ENABLED=0 GOOS="$TARGETOS" GOARCH="$TARGETARCH" go build -trimpath -ldflags='-s -w' -o /gosu .

FROM ${POSTGRES_IMAGE}
COPY debian-extensions.pref /etc/apt/preferences.d/honua-debian-extensions.pref
RUN set -eux; \
    apt-get update; \
    apt-get -y upgrade; \
    apt-get install -y --no-install-recommends \
      postgresql-17-postgis-3 postgresql-17-postgis-3-scripts postgresql-17-pgrouting; \
    rm -rf /var/lib/apt/lists/*
COPY --from=gosu /gosu /usr/local/bin/gosu
COPY --chmod=0755 honua-postgres-entrypoint.sh honua-collation-refresh.sh /usr/local/bin/
RUN gosu postgres true
ENTRYPOINT ["honua-postgres-entrypoint.sh"]
CMD ["postgres"]
