# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
#
# PostgreSQL client for the Launch Stack's start-up containers: one hands the scratch volumes to
# the server's user, the other creates the application role on the managed database. It carries
# only the Alpine base, psql and its libraries: no database server and no privilege-drop helper.
# .github/workflows/install-images.yml rebuilds it weekly so Alpine fixes reach the pin.
ARG ALPINE_IMAGE=alpine:3.24@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf
FROM ${ALPINE_IMAGE}
# psql 17 serves the Launch Stack's PostgreSQL 16 engine. It is the client in Alpine main, whose
# packages get security fixes for the whole release; community packages get them for less time.
RUN apk upgrade --no-cache && apk add --no-cache postgresql17-client && psql --version
USER 65534:65534
