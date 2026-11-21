# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
#
# Web server for the Compose quickstart's static Console (docs/get-started/compose.console.yaml).
# The upstream unprivileged image lags Alpine security updates, so this applies them; the nginx
# layout, entrypoint and non-root user are unchanged. .github/workflows/install-images.yml
# rebuilds it weekly.
ARG NGINX_IMAGE=nginxinc/nginx-unprivileged:1.31-alpine@sha256:b5047b9b76845eac124358373c6852377c52e4b05834a47647e955dd91cb8f1e
FROM ${NGINX_IMAGE}
USER 0:0
RUN apk upgrade --no-cache
USER 101:101
