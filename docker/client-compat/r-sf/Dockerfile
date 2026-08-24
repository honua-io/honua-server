# R sf/ows4R canonical-client interop lane (honua-server#3392).
#
# Base image is pinned to an explicit rocker/geospatial tag (never `latest`) so
# the GDAL/GEOS/PROJ stack under `sf` is reproducible run to run:
#
#   rocker/geospatial:4.6.1  ->  Ubuntu 24.04, R 4.6.1,
#                                sf 1.1.2, GDAL 3.8.4, GEOS 3.12.1, PROJ 9.4.0
#
# CRAN packages come from a date-pinned Posit Package Manager (P3M) snapshot
# rather than `latest`, so a rebuild resolves the same versions. Versions
# resolved at the pinned snapshot (2026-08-01, linux/noble binaries):
#
#   ows4R 0.5      jsonlite 2.0.0    digest 0.6.39    httr 1.4.8
#   testthat 3.3.2 (base image)      geometa 0.10.1 + keyring 1.4.1 (ows4R deps)
#
# The image is a runner only: the suite itself is bind-mounted by
# docker/client-compat/compose.yml, which must provide
#   ../../tests:/workspace/tests:ro     (the R suite + seed/config digest inputs)
#   ./output/r-sf:/output               (where the .cert.json envelopes land)

FROM rocker/geospatial:4.6.1

# Date-pinned CRAN snapshot. Bump deliberately (and re-record the versions in
# the header above) rather than drifting with `latest`.
ENV HONUA_CRAN_SNAPSHOT=https://p3m.dev/cran/__linux__/noble/2026-08-01 \
    R_KEEP_PKG_SOURCE=no \
    DEBIAN_FRONTEND=noninteractive

# libsecret-1-0 is a hard runtime requirement of `keyring`, which ows4R depends
# on: without it the ows4R namespace fails to load with
# "libsecret-1.so.0: cannot open shared object file".
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        jq \
        libsecret-1-0 \
 && apt-get clean \
 && rm -rf /var/lib/apt/lists/*

# ows4R drives the WFS capabilities/DescribeFeatureType surface; jsonlite and
# digest write the evidence envelope and its content-addressed receipt fields;
# httr backs the admin control-plane probe used for CERT-AUTH-*.
RUN Rscript -e "\
      options( \
        repos = c(CRAN = Sys.getenv('HONUA_CRAN_SNAPSHOT')), \
        Ncpus = max(1L, parallel::detectCores()) \
      ); \
      install.packages(c('ows4R', 'jsonlite', 'digest', 'httr', 'testthat')); \
      for (pkg in c('sf', 'ows4R', 'jsonlite', 'digest', 'httr', 'testthat')) { \
        if (!requireNamespace(pkg, quietly = TRUE)) stop('missing package: ', pkg); \
        cat(sprintf('%-10s %s\n', pkg, as.character(packageVersion(pkg)))); \
      }; \
      print(sf::sf_extSoftVersion())"

WORKDIR /workspace

COPY docker/client-compat/r-sf/run.sh /usr/local/bin/run-r-sf-lane
RUN chmod +x /usr/local/bin/run-r-sf-lane

ENTRYPOINT ["/usr/local/bin/run-r-sf-lane"]
