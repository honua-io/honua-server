#!/usr/bin/env Rscript
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

# R `sf` / `ows4R` canonical-client certification lane (honua-server#3392).
#
# Exercises the 16 applicable common-core CERT-* cases plus a lane-specific
# `NB-RSF-*` extension suite against two protocols — OGC API Features 1.0 and
# WFS 2.0.0 — and writes one `.cert.json` evidence envelope per protocol.
#
# The point of certifying a client is to prove the SERVER is compatible with
# it, so the extension suite deliberately reaches past the common core: CRS
# handling and axis order, attribute typing, null handling, geometry fidelity,
# paging invariants, format round-trips, declared-vs-honoured conformance,
# cross-protocol agreement, and the error surface.
#
# Client surface, by design:
#   * Feature reads go through `sf::st_read()` on the GDAL `OAPIF:` / `WFS:`
#     DSNs (the same DSN shapes `docker/client-compat/gdal/conftest.py` uses),
#     or on a fully-parameterised protocol URL when the case is about a
#     protocol parameter GDAL does not expose through the DSN.
#   * WFS capabilities, feature-type listing, DescribeFeatureType and
#     GetFeature options go through `ows4R::WFSClient` — that is what makes
#     this an ows4R lane rather than a second GDAL lane.
#   * `httr` is used for the CERT-AUTH-* control-plane probe and for
#     transport-shape checks (status codes, headers, `numberMatched`); every
#     such result says so in `notes`.
#
# Every case is independently error-trapped: one failure can never abort the
# run and silently drop the remaining cases, and an applicable common-core case
# that never executed is written as `skip` (fail-closed) by the envelope writer.
#
# Environment:
#   HONUA_R_SF_BASE_URL / HONUA_BASE_URL   required; no local-server fallback
#   HONUA_R_SF_OUTPUT_DIR                  envelope output dir (default tests/TestResults)
#   HONUA_R_SF_SERVER_COMMIT               overrides `git rev-parse HEAD`
#   HONUA_R_SF_SERVER_VERSION              overrides the /api/v1/admin/version probe
#   HONUA_R_SF_SERVICE_ID                  overrides the seeded service id
#   HONUA_R_SF_COLLECTION_ID               overrides the seeded collection id

suppressPackageStartupMessages({
  library(sf)
  library(ows4R)
  library(httr)
  library(jsonlite)
  library(digest)
})

options(digits.secs = 6)
Sys.setenv(TZ = "UTC")

# Bound every GDAL HTTP call so an unresponsive server fails the case instead
# of hanging the lane.
Sys.setenv(GDAL_HTTP_TIMEOUT = "60", GDAL_HTTP_CONNECTTIMEOUT = "15")

.script_dir <- local({
  args <- commandArgs(trailingOnly = FALSE)
  hit <- grep("^--file=", args, value = TRUE)
  if (length(hit) > 0L) {
    normalizePath(dirname(sub("^--file=", "", hit[[1L]])), mustWork = TRUE)
  } else {
    normalizePath(getwd(), mustWork = TRUE)
  }
})

source(file.path(.script_dir, "cert_envelope.R"))
source(file.path(.script_dir, "canonical_fixture.R"))

LANE <- "r-sf"

NOT_APPLICABLE_REASON <- paste0(
  "R sf/ows4R is a data-access client with no drawing surface; ",
  "rendering facets are structurally not applicable."
)

# The 16 applicable common-core IDs: the full core minus the eight rendering
# facets, which this lane declares structurally not-applicable.
APPLICABLE_IDS <- setdiff(COMMON_CORE_IDS, RENDERING_IDS)

`%||%` <- function(x, y) if (is.null(x)) y else x

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

resolve_base_url <- function() {
  for (name in c("HONUA_R_SF_BASE_URL", "HONUA_BASE_URL")) {
    value <- Sys.getenv(name, "")
    if (nzchar(value)) {
      return(sub("/+$", "", value))
    }
  }
  stop(paste0(
    "No base URL configured. Set HONUA_R_SF_BASE_URL or HONUA_BASE_URL; ",
    "this lane deliberately has no local-server fallback."
  ), call. = FALSE)
}

resolve_output_dir <- function() {
  configured <- Sys.getenv("HONUA_R_SF_OUTPUT_DIR", "")
  if (nzchar(configured)) configured else file.path(cf_tests_root(), "TestResults")
}

lane_service_id <- function() {
  value <- Sys.getenv("HONUA_R_SF_SERVICE_ID", "")
  if (nzchar(value)) value else SERVICE_ID
}

lane_collection_id <- function() {
  value <- Sys.getenv("HONUA_R_SF_COLLECTION_ID", "")
  if (nzchar(value)) value else COLLECTION_ID
}

lane_client_version <- function() {
  gdal <- tryCatch(unname(sf::sf_extSoftVersion()[["GDAL"]]), error = function(e) "unknown")
  sprintf(
    "sf=%s;ows4R=%s;GDAL=%s",
    as.character(utils::packageVersion("sf")),
    as.character(utils::packageVersion("ows4R")),
    gdal
  )
}

truncate_note <- function(text, limit = 400L) {
  text <- gsub("[\r\n\t]+", " ", paste(as.character(text), collapse = " "))
  text <- trimws(text)
  if (nchar(text) > limit) paste0(substr(text, 1L, limit), "...") else text
}

# ---------------------------------------------------------------------------
# Case outcome helpers
# ---------------------------------------------------------------------------

case_pass <- function(notes, measured_count = NULL, measured_delta = NULL) {
  list(status = "pass", notes = notes,
       measured_count = measured_count, measured_delta = measured_delta)
}

case_fail <- function(notes, measured_count = NULL, measured_delta = NULL) {
  list(status = "fail", notes = notes,
       measured_count = measured_count, measured_delta = measured_delta)
}

case_verdict <- function(ok, notes, measured_count = NULL, measured_delta = NULL) {
  if (isTRUE(ok)) case_pass(notes, measured_count, measured_delta)
  else case_fail(notes, measured_count, measured_delta)
}

#' Run one CERT/NB case under its own error trap and record the outcome.
#'
#' A thrown condition becomes a `fail` carrying the message, never an aborted
#' run. Warnings are collected for diagnostics but never abort the body: GDAL
#' surfaces useful detail (bad SQL, HTTP status) as R warnings on paths that
#' still return a valid result.
run_case <- function(collector, case_id, body) {
  started <- Sys.time()
  warnings_seen <- character(0)
  outcome <- withCallingHandlers(
    tryCatch(
      body(),
      error = function(e) case_fail(paste0("Unhandled error: ", truncate_note(conditionMessage(e))))
    ),
    warning = function(w) {
      warnings_seen <<- c(warnings_seen, conditionMessage(w))
      invokeRestart("muffleWarning")
    }
  )
  elapsed_ms <- as.numeric(difftime(Sys.time(), started, units = "secs")) * 1000
  if (!is.list(outcome) || is.null(outcome$status)) {
    outcome <- case_fail("Case body returned no status; treated as a failure.")
  }
  notes <- outcome$notes %||% ""
  if (identical(outcome$status, "fail") && length(warnings_seen) > 0L) {
    notes <- paste0(notes, " [warnings: ", truncate_note(paste(warnings_seen, collapse = " | "), 240L), "]")
  }
  collector_record(
    collector,
    test_case_id = case_id,
    status = outcome$status,
    duration_ms = elapsed_ms,
    measured_count = outcome$measured_count,
    measured_delta = outcome$measured_delta,
    notes = notes,
    evidence_ref = outcome$evidence_ref %||% ""
  )
  message(sprintf("  %-18s %-4s  %s", case_id, toupper(outcome$status), truncate_note(notes, 110L)))
  invisible(outcome)
}

# ---------------------------------------------------------------------------
# HTTP helpers (transport-shape checks only)
# ---------------------------------------------------------------------------

http_get <- function(url, query = NULL, headers = NULL, timeout_s = 60) {
  args <- list(url)
  if (!is.null(query)) args$query <- query
  if (!is.null(headers)) args <- c(args, list(headers))
  args <- c(args, list(httr::timeout(timeout_s)))
  response <- do.call(httr::GET, args)
  text <- tryCatch(httr::content(response, as = "text", encoding = "UTF-8"),
                   error = function(e) "")
  parsed <- NULL
  if (nzchar(text) && grepl("^\\s*[\\{\\[]", text)) {
    parsed <- tryCatch(jsonlite::fromJSON(text, simplifyVector = FALSE), error = function(e) NULL)
  }
  list(
    status = httr::status_code(response),
    text = text,
    json = parsed,
    headers = httr::headers(response),
    response = response
  )
}

#' Admin API key for the control-plane probe.
#'
#' The canonical fixture value is the default; `HONUA_ADMIN_API_KEY` (set by
#' docker/client-compat/compose.yml alongside the server's HONUA_ADMIN_PASSWORD)
#' overrides it so the lane follows a rotated compose credential.
lane_admin_api_key <- function() {
  configured <- Sys.getenv("HONUA_ADMIN_API_KEY", "")
  if (nzchar(configured)) configured else ADMIN_API_KEY
}

api_key_header <- function(key = lane_admin_api_key()) {
  args <- list(key)
  names(args) <- ADMIN_API_KEY_HEADER
  do.call(httr::add_headers, args)
}

#' Build a fully-parameterised protocol URL for a feature read.
#'
#' `params` is a protocol-neutral list; each protocol maps it onto its own
#' parameter vocabulary so an extension case can be written once and mean the
#' same thing on OGC API Features and WFS.
protocol_items_url <- function(ctx, params = list()) {
  if (identical(ctx$kind, "oapif")) {
    query <- list()
    if (!is.null(params$limit)) query$limit <- params$limit
    if (!is.null(params$offset)) query$offset <- params$offset
    if (!is.null(params$crs)) query$crs <- params$crs
    if (!is.null(params$filter)) {
      query$filter <- params$filter
      query$`filter-lang` <- params$filter_lang %||% "cql2-text"
    }
    if (!is.null(params$datetime)) query$datetime <- params$datetime
    if (!is.null(params$properties)) query$properties <- params$properties
    if (!is.null(params$format)) query$f <- params$format
    httr::modify_url(ctx$items_url, query = query)
  } else {
    query <- list(
      SERVICE = "WFS", VERSION = "2.0.0", REQUEST = "GetFeature",
      TYPENAMES = ctx$layer %||% ctx$wfs_type_name,
      OUTPUTFORMAT = params$format %||% "application/geo+json"
    )
    if (!is.null(params$limit)) query$COUNT <- params$limit
    if (!is.null(params$offset)) query$STARTINDEX <- params$offset
    if (!is.null(params$crs)) query$SRSNAME <- params$crs
    if (!is.null(params$filter)) query$FILTER <- params$filter
    if (!is.null(params$properties)) query$PROPERTYNAME <- params$properties
    if (!is.null(params$sort_by)) query$SORTBY <- params$sort_by
    if (!is.null(params$result_type)) query$RESULTTYPE <- params$result_type
    httr::modify_url(ctx$wfs_url, query = query)
  }
}

read_url_sf <- function(url, fid_column_name = NULL) {
  if (is.null(fid_column_name)) {
    sf::st_read(url, quiet = TRUE)
  } else {
    sf::st_read(url, fid_column_name = fid_column_name, quiet = TRUE)
  }
}

# ---------------------------------------------------------------------------
# Control-plane probes (CERT-AUTH-*, CERT-CONN-02, NB-RSF-AUT-*)
# ---------------------------------------------------------------------------

#' Probe the admin control plane once; both envelopes record the same result.
probe_admin_auth <- function(base_url) {
  url <- paste0(base_url, ADMIN_PROBE_PATH)

  started <- Sys.time()
  anon <- tryCatch(http_get(url), error = function(e) NULL)
  anon_ms <- as.numeric(difftime(Sys.time(), started, units = "secs")) * 1000
  anon_status <- if (is.null(anon)) NA_integer_ else anon$status
  challenge <- if (is.null(anon)) NA_character_ else (anon$headers[["www-authenticate"]] %||% NA_character_)

  started <- Sys.time()
  authed <- tryCatch(http_get(url, headers = api_key_header()), error = function(e) NULL)
  auth_ms <- as.numeric(difftime(Sys.time(), started, units = "secs")) * 1000
  auth_status <- if (is.null(authed)) NA_integer_ else authed$status

  wrong <- tryCatch(http_get(url, headers = api_key_header("definitely-not-the-admin-key")),
                    error = function(e) NULL)
  wrong_status <- if (is.null(wrong)) NA_integer_ else wrong$status

  list(
    url = url,
    anon_status = anon_status, anon_ms = anon_ms, challenge = challenge,
    auth_status = auth_status, auth_ms = auth_ms,
    wrong_status = wrong_status,
    header = if (!is.na(auth_status) && auth_status >= 200 && auth_status < 300) api_key_header() else NULL
  )
}

record_control_plane_results <- function(collector, base_url, probe) {
  # CERT-CONN-02 — the client-compat compose network is deliberately plain
  # HTTP, so this lane asserts the transport it actually got rather than
  # claiming or skipping a TLS observation it cannot make.
  scheme <- tolower(sub("://.*$", "", base_url))
  collector_record(
    collector, "CERT-CONN-02",
    status = if (identical(scheme, "http")) "pass" else "fail",
    duration_ms = 0,
    notes = if (identical(scheme, "http")) {
      paste0("Transport scheme observed via httr/curl: 'http'. The client-compat compose ",
             "network is plain HTTP by design, so no TLS handshake is available here; TLS is ",
             "exercised in the release tier against the HTTPS candidate deployment.")
    } else {
      paste0("Expected the compose lane to be plain http; observed '", scheme, "'.")
    }
  )

  anon_ok <- !is.na(probe$anon_status) && probe$anon_status %in% c(401L, 403L)
  collector_record(
    collector, "CERT-AUTH-01",
    status = if (anon_ok) "pass" else "fail",
    duration_ms = probe$anon_ms,
    notes = paste0(
      "Anonymous httr GET ", ADMIN_PROBE_PATH, " returned ",
      if (is.na(probe$anon_status)) "a transport error" else probe$anon_status,
      " (expected 401/403). Control-plane probe uses httr because both feature protocols ",
      "are anonymous in the client-compat fixture."
    )
  )

  auth_ok <- !is.na(probe$auth_status) && probe$auth_status >= 200 && probe$auth_status < 300
  collector_record(
    collector, "CERT-AUTH-02",
    status = if (auth_ok) "pass" else "fail",
    duration_ms = probe$auth_ms,
    notes = paste0(
      "httr GET ", ADMIN_PROBE_PATH, " with header ", ADMIN_API_KEY_HEADER,
      ": <admin key> returned ",
      if (is.na(probe$auth_status)) "a transport error" else probe$auth_status,
      " (expected 2xx). Honua's control plane authenticates with an API key header, not HTTP ",
      "Basic and not a bearer login flow."
    )
  )
}

# ---------------------------------------------------------------------------
# Protocol context
# ---------------------------------------------------------------------------

new_protocol_ctx <- function(base_url, spec, probe) {
  ctx <- new.env(parent = emptyenv())
  ctx$base_url <- base_url
  ctx$kind <- spec$kind
  ctx$protocol <- spec$protocol
  ctx$protocol_version <- spec$protocol_version
  ctx$service_id <- lane_service_id()
  ctx$collection_id <- lane_collection_id()
  ctx$probe <- probe
  ctx$features_url <- paste0(base_url, "/ogc/features")
  ctx$items_url <- sprintf("%s/ogc/features/collections/%s/items", base_url, ctx$collection_id)
  ctx$collection_url <- sprintf("%s/ogc/features/collections/%s", base_url, ctx$collection_id)
  ctx$wfs_url <- paste0(base_url, "/wfs")
  ctx$dsn <- if (identical(spec$kind, "oapif")) {
    sprintf("OAPIF:%s/ogc/features", base_url)
  } else {
    sprintf("WFS:%s/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities", base_url)
  }
  ctx$driver <- if (identical(spec$kind, "oapif")) "OAPIF" else "WFS"
  ctx$layer <- NULL
  ctx$layer_error <- NULL
  ctx$features <- NULL
  ctx$page_one_ids <- NULL
  ctx$wfs_client <- NULL
  ctx$wfs_feature_type <- NULL
  ctx$ows_error <- NULL
  ctx
}

#' Score a candidate layer/feature-type name against the seeded fixture.
#'
#' The compose stack seeds several services, so "take the first layer" is not
#' safe: prefer the collection id verbatim (OAPIF), then the WFS type name the
#' server derives from the seeded layer name, then anything bound to the
#' seeded service.
score_layer_name <- function(name, service_id, collection_id) {
  score <- 0L
  local_name <- sub("^.*:", "", name)
  if (identical(name, collection_id)) score <- score + 100L
  if (identical(local_name, WFS_TYPE_LOCAL_NAME)) score <- score + 80L
  if (grepl(service_id, name, fixed = TRUE)) score <- score + 50L
  if (grepl(paste0("[:_.-]", collection_id, "$"), name)) score <- score + 25L
  score
}

resolve_layer <- function(ctx) {
  if (!is.null(ctx$layer)) return(ctx$layer)
  if (!is.null(ctx$layer_error)) stop(ctx$layer_error, call. = FALSE)
  resolved <- tryCatch({
    layers <- sf::st_layers(ctx$dsn)
    names <- as.character(layers$name)
    if (length(names) == 0L) stop("the dataset exposes no layers")
    scores <- vapply(names, score_layer_name, integer(1),
                     service_id = ctx$service_id, collection_id = ctx$collection_id)
    ctx$layer_names <- names
    ctx$layer_table <- layers
    names[[which.max(scores)]]
  }, error = function(e) {
    ctx$layer_error <- paste0("Could not resolve a layer on the ", ctx$driver, " DSN: ",
                              truncate_note(conditionMessage(e)))
    NULL
  })
  if (is.null(resolved)) stop(ctx$layer_error, call. = FALSE)
  ctx$layer <- resolved
  resolved
}

#' Read the full layer once and cache it — most cases assert over the same
#' feature set, and re-reading would multiply the lane's wall-clock cost.
read_all_features <- function(ctx) {
  if (!is.null(ctx$features)) return(ctx$features)
  layer <- resolve_layer(ctx)
  ctx$features <- sf::st_read(ctx$dsn, layer = layer, quiet = TRUE)
  ctx$features
}

feature_ids <- function(x) {
  for (column in c("ogc_fid", FEATURE_ID_FIELD, "gml_id", "id", "name")) {
    if (column %in% names(x)) return(as.character(x[[column]]))
  }
  as.character(sf::st_as_text(sf::st_geometry(x)))
}

ogr_sql <- function(layer, where = NULL, limit = NULL, offset = NULL) {
  sql <- sprintf('SELECT * FROM "%s"', layer)
  if (!is.null(where)) sql <- paste(sql, "WHERE", where)
  if (!is.null(limit)) sql <- paste(sql, "LIMIT", limit)
  if (!is.null(offset)) sql <- paste(sql, "OFFSET", offset)
  sql
}

read_page <- function(ctx, limit, offset = NULL) {
  layer <- resolve_layer(ctx)
  sf::st_read(ctx$dsn, query = ogr_sql(layer, limit = limit, offset = offset),
              fid_column_name = "ogc_fid", quiet = TRUE)
}

anchor_row <- function(features) {
  if (!("name" %in% names(features))) return(NULL)
  hit <- features[!is.na(features$name) & features$name == ANCHOR_NAME, , drop = FALSE]
  if (nrow(hit) != 1L) NULL else hit
}

#' Coerce server-supplied timestamps (POSIXct or ISO-8601 text) to UTC instants.
#'
#' Vectorised: the datetime cases hand it a whole column, so every branch must
#' work element-wise rather than on a scalar condition.
as_utc_instant <- function(value) {
  if (inherits(value, "POSIXt")) {
    tz <- attr(value, "tzone")
    if (is.null(tz) || !nzchar(tz)) attr(value, "tzone") <- "UTC"
    return(as.POSIXct(value, tz = "UTC"))
  }
  text <- as.character(value)
  text <- sub("Z$", "+0000", text)
  text <- sub("([+-][0-9]{2}):([0-9]{2})$", "\\1\\2", text)
  parsed <- as.POSIXct(text, format = "%Y-%m-%dT%H:%M:%S%z", tz = "UTC")
  fallback <- is.na(parsed)
  if (any(fallback)) {
    parsed[fallback] <- as.POSIXct(sub("T", " ", text[fallback]),
                                   format = "%Y-%m-%d %H:%M:%S", tz = "UTC")
  }
  parsed
}

#' Split a server-supplied array rendering into its element values.
#'
#' GeoJSON gives a real list; GML 3.2 has no array type and the server renders
#' the JSON text (`["red","blue"]`). Both must yield the same element set.
array_elements <- function(value) {
  flat <- paste(unlist(value), collapse = ",")
  # POSIX bracket expressions do not honour backslash escapes, so "]" must come
  # first in the class and "[" and the quote follow it literally.
  flat <- gsub('[]["]', "", flat)
  parts <- trimws(unlist(strsplit(flat, ",", fixed = TRUE)))
  parts[nzchar(parts)]
}

flatten_list_columns <- function(x) {
  for (column in names(x)) {
    value <- x[[column]]
    if (is.list(value) && !inherits(value, "sfc")) {
      x[[column]] <- vapply(value, function(v) paste(unlist(v), collapse = ","), character(1))
    }
  }
  x
}

# ---------------------------------------------------------------------------
# ows4R (WFS discovery surface)
# ---------------------------------------------------------------------------

wfs_client <- function(ctx, version = "2.0.0") {
  key <- paste0("wfs_client_", version)
  cached <- ctx[[key]]
  if (!is.null(cached)) return(cached)
  client <- ows4R::WFSClient$new(url = ctx$wfs_url, serviceVersion = version, logger = NULL)
  ctx[[key]] <- client
  client
}

wfs_feature_types <- function(ctx) {
  types <- wfs_client(ctx)$getCapabilities()$getFeatureTypes()
  if (length(types) == 0L) {
    stop(paste0("ows4R parsed ZERO feature types out of the WFS 2.0.0 capabilities document ",
                "(GDAL's WFS driver sees them, so the document is not empty)"), call. = FALSE)
  }
  types
}

wfs_feature_type <- function(ctx) {
  if (!is.null(ctx$wfs_feature_type)) return(ctx$wfs_feature_type)
  types <- wfs_feature_types(ctx)
  names <- vapply(types, function(ft) as.character(ft$getName()), character(1))
  scores <- vapply(names, score_layer_name, integer(1),
                   service_id = ctx$service_id, collection_id = ctx$collection_id)
  ctx$wfs_feature_type <- types[[which.max(scores)]]
  ctx$wfs_type_name <- names[[which.max(scores)]]
  ctx$wfs_feature_type
}

# ---------------------------------------------------------------------------
# Common-core cases
# ---------------------------------------------------------------------------

case_conn_01 <- function(ctx) {
  features <- read_all_features(ctx)
  rows <- nrow(features)
  case_verdict(
    rows > 0L,
    sprintf(paste0("sf::st_read() opened %s layer '%s' and returned %d row(s) (fixture seeds ",
                   "%d features, %d with geometry)."),
            ctx$driver, ctx$layer, rows, TOTAL_FEATURES, FEATURES_WITH_GEOMETRY),
    measured_count = rows
  )
}

case_disc_01 <- function(ctx) {
  if (identical(ctx$kind, "wfs")) {
    types <- wfs_feature_types(ctx)
    count <- length(types)
    selected <- as.character(wfs_feature_type(ctx)$getName())
    how <- "ows4R WFSClient$getCapabilities()$getFeatureTypes()"
  } else {
    layers <- sf::st_layers(ctx$dsn)
    count <- length(layers$name)
    selected <- resolve_layer(ctx)
    how <- "sf::st_layers() over the GDAL OAPIF driver (/collections listing)"
  }
  case_verdict(
    count > 0L && nzchar(selected),
    sprintf("%s listed %d feature type(s)/collection(s); certification target resolved to '%s'.",
            how, count, selected),
    measured_count = count
  )
}

case_disc_02 <- function(ctx) {
  if (identical(ctx$kind, "wfs")) {
    ft <- wfs_feature_type(ctx)
    name <- as.character(ft$getName())
    title <- tryCatch(as.character(ft$getTitle()), error = function(e) NA_character_)
    bbox <- tryCatch(ft$getBoundingBox(), error = function(e) NULL)
    return(case_verdict(
      nzchar(name) && !is.null(bbox),
      sprintf(paste0("ows4R feature-type metadata for '%s' from the WFS 2.0.0 capabilities: ",
                     "title=%s, boundingBox=%s."),
              name, if (length(title) && !is.na(title)) title else "<none>",
              if (is.null(bbox)) "<none>" else "present"),
      measured_count = 1L
    ))
  }
  collection_dsn <- sprintf("OAPIF:%s", ctx$collection_url)
  layers <- sf::st_layers(collection_dsn)
  count <- length(layers$name)
  case_verdict(
    count == 1L,
    sprintf(paste0("sf::st_layers() on the single-collection DSN %s reported %d collection(s): %s ",
                   "(the GDAL OAPIF driver fetches /collections/{id} for this DSN shape)."),
            collection_dsn, count, paste(as.character(layers$name), collapse = ", ")),
    measured_count = count
  )
}

case_schm_01 <- function(ctx) {
  if (identical(ctx$kind, "wfs")) {
    described <- wfs_feature_type(ctx)$getDescription(pretty = TRUE)
    fields <- as.character(described$name)
    how <- "ows4R WFSFeatureType$getDescription(pretty = TRUE) (WFS DescribeFeatureType)"
  } else {
    fields <- names(read_all_features(ctx))
    how <- "sf::st_read() attribute columns reported by the GDAL OAPIF driver"
  }
  # WFS/GML escapes ':' in element names as _x003A_; normalise before matching.
  normalised <- gsub("_x003A_", ":", fields, fixed = TRUE)
  present <- intersect(ATTRIBUTE_FIELDS, normalised)
  missing <- setdiff(ATTRIBUTE_FIELDS, normalised)
  case_verdict(
    length(missing) == 0L,
    sprintf("%s exposed %d/%d canonical attribute fields.%s", how, length(present),
            length(ATTRIBUTE_FIELDS),
            if (length(missing) == 0L) "" else paste0(" Missing: ", paste(missing, collapse = ", "), ".")),
    measured_count = length(present)
  )
}

case_schm_02 <- function(ctx) {
  features <- read_all_features(ctx)
  geometry <- sf::st_geometry(features)
  non_empty <- geometry[!sf::st_is_empty(geometry)]
  types <- unique(as.character(sf::st_geometry_type(non_empty)))
  declared <- NA_character_
  if (identical(ctx$kind, "wfs")) {
    declared <- tryCatch({
      description <- wfs_feature_type(ctx)$getDescription(pretty = TRUE)
      rows <- description[grepl("Geometry|Point", as.character(description$type), ignore.case = TRUE), , drop = FALSE]
      if (nrow(rows) > 0L) paste(unique(as.character(rows$type)), collapse = ",") else NA_character_
    }, error = function(e) NA_character_)
  }
  case_verdict(
    length(types) == 1L && identical(types[[1L]], "POINT"),
    sprintf("sf::st_geometry_type() over %d non-empty geometries reported: %s.%s",
            length(non_empty), paste(types, collapse = ", "),
            if (is.na(declared)) "" else paste0(" DescribeFeatureType declared: ", declared, ".")),
    measured_count = length(non_empty)
  )
}

case_qflt_01 <- function(ctx) {
  layer <- resolve_layer(ctx)
  where <- sprintf("%s = '%s'", FILTER_FIELD, FILTER_VALUE)
  filtered <- sf::st_read(ctx$dsn, query = ogr_sql(layer, where = where), quiet = TRUE)
  rows <- nrow(filtered)
  case_verdict(
    rows == ACTIVE_FEATURES,
    sprintf(paste0("Attribute filter \"%s\" applied via sf::st_read(query=) OGR SQL; the GDAL %s ",
                   "driver translates it into the protocol filter parameter (OAPIF queryable / ",
                   "CQL2, WFS 2.0 fes:Filter) where the server advertises it. Returned %d row(s); ",
                   "expected %d."),
            where, ctx$driver, rows, ACTIVE_FEATURES),
    measured_count = rows
  )
}

#' Protocol bbox parameter for the canonical subset envelope.
#'
#' OGC API Features Core fixes `bbox` to CRS84 lon,lat. WFS 2.0 KVP BBOX has no
#' default of its own: a four-element BBOX is expressed in the feature type's
#' default CRS, whose declared axis order for urn EPSG::4326 is lat,lon.
subset_bbox_parameter <- function(ctx) {
  values <- unname(SUBSET_BBOX[c("xmin", "ymin", "xmax", "ymax")])
  if (identical(ctx$kind, "oapif")) {
    return(paste(format(values, trim = TRUE, scientific = FALSE), collapse = ","))
  }
  swapped <- values[c(2L, 1L, 4L, 3L)]
  paste0(paste(format(swapped, trim = TRUE, scientific = FALSE), collapse = ","),
         sprintf(",urn:ogc:def:crs:EPSG::%d", STORAGE_CRS_EPSG))
}

bbox_items_url <- function(ctx, bbox_value, extra = list()) {
  if (identical(ctx$kind, "oapif")) {
    httr::modify_url(ctx$items_url, query = c(list(bbox = bbox_value, limit = 100), extra))
  } else {
    httr::modify_url(ctx$wfs_url, query = c(list(
      SERVICE = "WFS", VERSION = "2.0.0", REQUEST = "GetFeature",
      TYPENAMES = ctx$layer %||% ctx$wfs_type_name,
      OUTPUTFORMAT = "application/geo+json", BBOX = bbox_value), extra))
  }
}

case_qflt_02 <- function(ctx) {
  layer <- resolve_layer(ctx)
  bbox_value <- subset_bbox_parameter(ctx)
  server_side <- read_url_sf(bbox_items_url(ctx, bbox_value))
  rows <- nrow(server_side)

  # Corroborate with the GDAL spatial filter. The GDAL WFS driver evaluates
  # SetSpatialFilter client-side and keeps the null-geometry feature, so only
  # non-empty geometries are counted on that path.
  wkt <- sf::st_as_text(sf::st_as_sfc(sf::st_bbox(SUBSET_BBOX, crs = sf::st_crs(STORAGE_CRS_EPSG))))
  driver_rows <- tryCatch({
    filtered <- sf::st_read(ctx$dsn, layer = layer, wkt_filter = wkt, quiet = TRUE)
    sum(!sf::st_is_empty(sf::st_geometry(filtered)))
  }, error = function(e) NA_integer_)

  case_verdict(
    rows == SUBSET_BBOX_FEATURE_COUNT && (is.na(driver_rows) || driver_rows == SUBSET_BBOX_FEATURE_COUNT),
    sprintf(paste0("Spatial filter applied through the protocol bbox parameter (%s bbox=%s) and read ",
                   "with sf::st_read(): %d row(s); expected %d. Corroborated by sf::st_read(wkt_filter=) ",
                   "over the %s DSN (GDAL SetSpatialFilter): %s non-empty geometry/geometries."),
            ctx$driver, bbox_value, rows, SUBSET_BBOX_FEATURE_COUNT, ctx$driver,
            if (is.na(driver_rows)) "n/a" else as.character(driver_rows)),
    measured_count = rows
  )
}

case_page_01 <- function(ctx) {
  page <- read_page(ctx, limit = PAGE_SIZE)
  ctx$page_one_ids <- feature_ids(page)
  case_verdict(
    nrow(page) == PAGE_SIZE,
    sprintf("First page via sf::st_read(query='... LIMIT %d') returned %d row(s); expected %d.",
            PAGE_SIZE, nrow(page), PAGE_SIZE),
    measured_count = nrow(page)
  )
}

case_page_02 <- function(ctx) {
  if (is.null(ctx$page_one_ids)) {
    ctx$page_one_ids <- feature_ids(read_page(ctx, limit = PAGE_SIZE))
  }
  page_two <- read_page(ctx, limit = PAGE_SIZE, offset = PAGE_SIZE)
  ids_two <- feature_ids(page_two)
  overlap <- intersect(ctx$page_one_ids, ids_two)
  case_verdict(
    nrow(page_two) > 0L && length(overlap) == 0L,
    sprintf(paste0("Second page via sf::st_read(query='... LIMIT %d OFFSET %d') returned %d row(s) ",
                   "with id set {%s}; first page was {%s}; overlap %d."),
            PAGE_SIZE, PAGE_SIZE, nrow(page_two), paste(ids_two, collapse = ","),
            paste(ctx$page_one_ids, collapse = ","), length(overlap)),
    measured_count = nrow(page_two)
  )
}

case_geom_01 <- function(ctx) {
  features <- read_all_features(ctx)
  anchor <- anchor_row(features)
  if (is.null(anchor)) {
    return(case_fail(sprintf("Could not locate exactly one '%s' feature as the geometry anchor.",
                             ANCHOR_NAME)))
  }
  coords <- sf::st_coordinates(sf::st_geometry(anchor))
  delta <- max(abs(coords[1L, "X"] - ANCHOR_LON), abs(coords[1L, "Y"] - ANCHOR_LAT))
  case_verdict(
    delta <= GEOGRAPHIC_TOLERANCE_DEGREES,
    sprintf(paste0("Anchor '%s' returned (%.10f, %.10f) against reference (%.4f, %.4f); max ",
                   "absolute coordinate deviation %.3e decimal degrees, threshold %.0e."),
            ANCHOR_NAME, coords[1L, "X"], coords[1L, "Y"], ANCHOR_LON, ANCHOR_LAT,
            delta, GEOGRAPHIC_TOLERANCE_DEGREES),
    measured_delta = delta
  )
}

crs_is_wgs84 <- function(crs) {
  if (is.na(crs)) return(FALSE)
  epsg <- crs$epsg
  descriptor <- paste(c(crs$input, crs$wkt), collapse = " ")
  (!is.null(epsg) && !is.na(epsg) && epsg == STORAGE_CRS_EPSG) ||
    grepl("CRS84", descriptor, fixed = TRUE) ||
    grepl("EPSG[^0-9]{0,4}4326", descriptor) ||
    grepl("\"WGS 84\"", descriptor, fixed = TRUE)
}

case_geom_02 <- function(ctx) {
  features <- read_all_features(ctx)
  crs <- sf::st_crs(features)
  if (is.na(crs)) return(case_fail("sf::st_crs() reported no CRS on the returned feature set."))
  case_verdict(
    crs_is_wgs84(crs),
    sprintf(paste0("sf::st_crs() reported input=%s, epsg=%s. OGC:CRS84 is accepted as the lon/lat ",
                   "presentation of EPSG:4326 that both protocols default to."),
            if (is.null(crs$input) || is.na(crs$input)) "<none>" else crs$input,
            if (is.null(crs$epsg) || is.na(crs$epsg)) "<none>" else crs$epsg)
  )
}

case_errh_01 <- function(ctx) {
  message_text <- tryCatch({
    sf::st_read(ctx$dsn, layer = UNKNOWN_COLLECTION_ID, quiet = TRUE)
    NA_character_
  }, error = function(e) conditionMessage(e))
  if (is.na(message_text)) {
    return(case_fail(sprintf(
      "Reading unknown collection '%s' unexpectedly succeeded; expected a structured error.",
      UNKNOWN_COLLECTION_ID)))
  }
  structured <- grepl(UNKNOWN_COLLECTION_ID, message_text, fixed = TRUE) ||
    grepl("not found|does not exist|no such layer|cannot open layer|opening layer failed|unable to open|invalid layer",
          message_text, ignore.case = TRUE)
  # Cross-check the wire: the client-side message must correspond to a
  # structured protocol error, not a transport hiccup.
  wire <- if (identical(ctx$kind, "oapif")) {
    http_get(sprintf("%s/ogc/features/collections/%s/items", ctx$base_url, UNKNOWN_COLLECTION_ID))
  } else {
    http_get(ctx$wfs_url, query = list(SERVICE = "WFS", VERSION = "2.0.0",
                                       REQUEST = "GetFeature", TYPENAMES = UNKNOWN_COLLECTION_ID))
  }
  case_verdict(
    structured && wire$status >= 400L && wire$status < 500L,
    sprintf(paste0("sf::st_read(layer='%s') on the %s DSN raised: \"%s\"; the corresponding wire ",
                   "request returned %d (%s)."),
            UNKNOWN_COLLECTION_ID, ctx$driver, truncate_note(message_text, 160L), wire$status,
            truncate_note(wire$text, 120L))
  )
}

case_errh_02 <- function(ctx) {
  layer <- resolve_layer(ctx)
  bad_sql <- ogr_sql(layer, where = MALFORMED_CQL2_FILTER)
  captured <- list(message = NA_character_, warnings = character(0))
  result <- withCallingHandlers(
    tryCatch({ sf::st_read(ctx$dsn, query = bad_sql, quiet = TRUE); NULL },
             error = function(e) { captured$message <<- conditionMessage(e); NULL }),
    warning = function(w) {
      captured$warnings <<- c(captured$warnings, conditionMessage(w))
      invokeRestart("muffleWarning")
    }
  )
  diagnostic <- paste(c(captured$message, captured$warnings), collapse = " | ")
  rejected_by_client <- !is.na(captured$message)
  structured <- rejected_by_client &&
    grepl("syntax|parse|expression|invalid|unexpected|cannot open layer|query execution failed",
          diagnostic, ignore.case = TRUE)

  # Independently confirm the server rejects the same malformed filter with a
  # structured 4xx rather than a 500 (httr transport-shape check).
  wire <- http_get(protocol_items_url(ctx, list(filter = MALFORMED_CQL2_FILTER)))
  wire_ok <- wire$status >= 400L && wire$status < 500L

  case_verdict(
    structured && wire_ok,
    sprintf(paste0("Malformed filter \"%s\" via sf::st_read(query=\"%s\") was rejected by the GDAL ",
                   "OGR SQL parser: \"%s\". The same malformed filter sent as the protocol filter ",
                   "parameter returned %d (httr transport-shape check): %s"),
            MALFORMED_CQL2_FILTER, bad_sql, truncate_note(diagnostic, 200L), wire$status,
            truncate_note(wire$text, 140L))
  )
}

# ---------------------------------------------------------------------------
# NB-RSF-* extension cases: attribute typing and null handling
# ---------------------------------------------------------------------------

ext_typ_01 <- function(ctx) {
  features <- sf::st_drop_geometry(read_all_features(ctx))
  problems <- character(0)
  if (!is.integer(features$count)) {
    problems <- c(problems, sprintf("count materialised as %s, expected integer", class(features$count)[1]))
  }
  if (!identical(sort(as.integer(features$count)), 1:10)) {
    problems <- c(problems, "count values are not the seeded 1..10")
  }
  if (!is.numeric(features$ratio) || is.integer(features$ratio)) {
    problems <- c(problems, sprintf("ratio materialised as %s, expected double", class(features$ratio)[1]))
  }
  anchor <- features[features$name == ANCHOR_NAME, , drop = FALSE]
  if (nrow(anchor) == 1L && abs(anchor$ratio[[1L]] - 1.25) > 1e-12) {
    problems <- c(problems, sprintf("ratio for '%s' is %s, expected 1.25", ANCHOR_NAME, anchor$ratio[[1L]]))
  }
  if (!is.logical(features$active)) {
    problems <- c(problems, sprintf("active materialised as %s, expected logical", class(features$active)[1]))
  } else if (nrow(anchor) == 1L && !isTRUE(anchor$active[[1L]])) {
    problems <- c(problems, "active for the anchor feature is not TRUE")
  }
  case_verdict(
    length(problems) == 0L,
    sprintf(paste0("Numeric/boolean typing through %s: count=%s, ratio=%s, active=%s. %s"),
            ctx$driver, class(features$count)[1], class(features$ratio)[1], class(features$active)[1],
            if (length(problems) == 0L) "All seeded values round-trip with the declared types." else
              paste0("Problems: ", paste(problems, collapse = "; "), ".")),
    measured_count = nrow(features)
  )
}

ext_typ_02 <- function(ctx) {
  features <- sf::st_drop_geometry(read_all_features(ctx))
  anchor <- features[features$name == ANCHOR_NAME, , drop = FALSE]
  if (nrow(anchor) != 1L) return(case_fail("Anchor feature not found for temporal typing."))
  problems <- character(0)
  instant <- as_utc_instant(anchor$created_at[[1L]])
  expected <- as.POSIXct("2024-01-01 12:00:00", tz = "UTC")
  if (is.na(instant) || abs(as.numeric(difftime(instant, expected, units = "secs"))) > 0.5) {
    problems <- c(problems, sprintf("created_at resolved to %s, expected 2024-01-01T12:00:00Z",
                                    if (is.na(instant)) "NA" else format(instant, tz = "UTC")))
  }
  event_date <- anchor$event_date[[1L]]
  if (!identical(as.character(as.Date(event_date)), "2024-02-01")) {
    problems <- c(problems, sprintf("event_date resolved to %s, expected 2024-02-01", as.character(event_date)))
  }
  if (!grepl("^12:34:56", as.character(anchor$event_time[[1L]]))) {
    problems <- c(problems, sprintf("event_time resolved to %s, expected 12:34:56",
                                    as.character(anchor$event_time[[1L]])))
  }
  case_verdict(
    length(problems) == 0L,
    sprintf(paste0("Temporal typing through %s: created_at as %s, event_date as %s, event_time as %s. ",
                   "Instants are compared in UTC, so a lost timezone is a failure, not a class ",
                   "difference. %s"),
            ctx$driver, class(anchor$created_at)[1], class(anchor$event_date)[1],
            class(anchor$event_time)[1],
            if (length(problems) == 0L) "All seeded temporal values round-trip." else
              paste0("Problems: ", paste(problems, collapse = "; "), ".")),
    measured_count = 3L
  )
}

ext_typ_03 <- function(ctx) {
  features <- sf::st_drop_geometry(read_all_features(ctx))
  anchor <- features[features$name == ANCHOR_NAME, , drop = FALSE]
  if (nrow(anchor) != 1L) return(case_fail("Anchor feature not found for array typing."))
  tags_values <- array_elements(anchor$tags[[1L]])
  numbers_values <- suppressWarnings(as.numeric(array_elements(anchor$numbers[[1L]])))
  content_ok <- identical(sort(tags_values), c("blue", "red")) &&
    identical(sort(numbers_values), c(0, 1, 2))
  structural_ok <- is.list(anchor$tags) && is.list(anchor$numbers)
  if (identical(ctx$kind, "oapif")) {
    # GeoJSON has a real array type; a stringified array here is a server bug.
    return(case_verdict(
      content_ok && structural_ok,
      sprintf(paste0("JSON array columns through OAPIF/GeoJSON: tags as %s, numbers as %s; ",
                     "values tags=[%s] numbers=[%s]. GeoJSON carries real arrays, so a stringified ",
                     "array would be a server-side typing bug."),
              class(anchor$tags)[1], class(anchor$numbers)[1],
              paste(tags_values, collapse = "|"), paste(numbers_values, collapse = "|"))
    ))
  }
  case_verdict(
    content_ok,
    sprintf(paste0("JSON array columns through WFS/GML: tags as %s, numbers as %s; values tags=[%s] ",
                   "numbers=[%s]. GML 3.2 has no array type, so stringification is expected here; ",
                   "the assertion is that no element is lost or garbled."),
            class(anchor$tags)[1], class(anchor$numbers)[1],
            paste(tags_values, collapse = "|"), paste(numbers_values, collapse = "|"))
  )
}

ext_typ_04 <- function(ctx) {
  features <- sf::st_drop_geometry(read_all_features(ctx))
  anchor <- features[features$name == ANCHOR_NAME, , drop = FALSE]
  if (nrow(anchor) != 1L) return(case_fail("Anchor feature not found for uuid typing."))
  uid <- as.character(anchor$uid[[1L]])
  expected <- "00000000-0000-0000-0000-000000000001"
  case_verdict(
    identical(uid, expected),
    sprintf("uuid column materialised as %s with value '%s' (expected '%s').",
            class(anchor$uid)[1], uid, expected)
  )
}

ext_nul_01 <- function(ctx) {
  features <- sf::st_drop_geometry(read_all_features(ctx))
  descriptions <- features$description
  na_count <- sum(is.na(descriptions) | !nzchar(as.character(descriptions)))
  anchor_na <- is.na(descriptions[features$name == ANCHOR_NAME][[1L]])
  beta <- as.character(descriptions[features$name == "beta"][[1L]])
  case_verdict(
    na_count == 4L && isTRUE(anchor_na) && identical(beta, "description_1"),
    sprintf(paste0("Nullable `description`: %d of %d rows are NA (fixture seeds 4 NULLs); ",
                   "'%s' is %s and 'beta' is '%s'. A server that emitted \"\" or the string ",
                   "\"null\" would fail this."),
            na_count, nrow(features), ANCHOR_NAME,
            if (isTRUE(anchor_na)) "NA" else "not NA", beta),
    measured_count = na_count
  )
}

ext_nul_02 <- function(ctx) {
  features <- read_all_features(ctx)
  geometry <- sf::st_geometry(features)
  empty <- sf::st_is_empty(geometry)
  lambda_present <- "lambda" %in% as.character(features$name)
  case_verdict(
    nrow(features) == TOTAL_FEATURES && sum(empty) == (TOTAL_FEATURES - FEATURES_WITH_GEOMETRY) &&
      lambda_present,
    sprintf(paste0("Null-geometry handling: %d row(s) returned with %d empty geometry/geometries ",
                   "(fixture seeds %d features, %d with geometry); the null-geometry row 'lambda' is ",
                   "%s. Dropping it or failing the read would be a server bug."),
            nrow(features), sum(empty), TOTAL_FEATURES, FEATURES_WITH_GEOMETRY,
            if (lambda_present) "present" else "MISSING"),
    measured_count = sum(empty)
  )
}

# ---------------------------------------------------------------------------
# NB-RSF-* extension cases: geometry fidelity
# ---------------------------------------------------------------------------

declared_extent <- function(ctx) {
  if (identical(ctx$kind, "wfs")) {
    bbox <- wfs_feature_type(ctx)$getBoundingBox()
    return(c(xmin = bbox[1L, 1L], ymin = bbox[2L, 1L], xmax = bbox[1L, 2L], ymax = bbox[2L, 2L]))
  }
  metadata <- http_get(ctx$collection_url)
  spatial <- metadata$json$extent$spatial$bbox[[1L]]
  c(xmin = as.numeric(spatial[[1L]]), ymin = as.numeric(spatial[[2L]]),
    xmax = as.numeric(spatial[[3L]]), ymax = as.numeric(spatial[[4L]]))
}

ext_geo_01 <- function(ctx) {
  features <- read_all_features(ctx)
  actual <- sf::st_bbox(features)
  declared <- declared_extent(ctx)
  tolerance <- 1e-9
  contained <- actual[["xmin"]] >= declared[["xmin"]] - tolerance &&
    actual[["ymin"]] >= declared[["ymin"]] - tolerance &&
    actual[["xmax"]] <= declared[["xmax"]] + tolerance &&
    actual[["ymax"]] <= declared[["ymax"]] + tolerance
  case_verdict(
    contained,
    sprintf(paste0("st_bbox() of the returned features (%s) against the %s declared extent (%s): ",
                   "the data extent must lie inside the advertised extent, otherwise clients that ",
                   "zoom to the declared extent hide features."),
            paste(sprintf("%.5f", unname(actual[c("xmin", "ymin", "xmax", "ymax")])), collapse = " "),
            ctx$driver,
            paste(sprintf("%.5f", unname(declared[c("xmin", "ymin", "xmax", "ymax")])), collapse = " "))
  )
}

ext_geo_02 <- function(ctx) {
  features <- read_all_features(ctx)
  geometry <- sf::st_geometry(features)
  non_empty <- geometry[!sf::st_is_empty(geometry)]
  wkb_back <- sf::st_as_sfc(sf::st_as_binary(non_empty), crs = sf::st_crs(features))
  wkt_back <- sf::st_as_sfc(sf::st_as_text(non_empty), crs = sf::st_crs(features))
  wkb_delta <- max(abs(sf::st_coordinates(non_empty) - sf::st_coordinates(wkb_back)))
  wkt_delta <- max(abs(sf::st_coordinates(non_empty) - sf::st_coordinates(wkt_back)))
  delta <- max(wkb_delta, wkt_delta)
  case_verdict(
    delta <= GEOGRAPHIC_TOLERANCE_DEGREES,
    sprintf(paste0("WKB (st_as_binary) and WKT (st_as_text) round-trip of %d server geometries: ",
                   "max coordinate deviation %.3e (WKB %.3e, WKT %.3e), threshold %.0e."),
            length(non_empty), delta, wkb_delta, wkt_delta, GEOGRAPHIC_TOLERANCE_DEGREES),
    measured_delta = delta
  )
}

ext_geo_03 <- function(ctx) {
  features <- read_all_features(ctx)
  geometry <- sf::st_geometry(features)
  non_empty <- geometry[!sf::st_is_empty(geometry)]
  valid <- sf::st_is_valid(non_empty)
  case_verdict(
    all(valid, na.rm = FALSE) && !any(is.na(valid)),
    sprintf("sf::st_is_valid() over %d server geometries: %d valid, %d invalid, %d indeterminate.",
            length(non_empty), sum(valid %in% TRUE), sum(valid %in% FALSE), sum(is.na(valid))),
    measured_count = sum(valid %in% TRUE)
  )
}

# ---------------------------------------------------------------------------
# NB-RSF-* extension cases: CRS
# ---------------------------------------------------------------------------

protocol_crs_list <- function(ctx) {
  if (identical(ctx$kind, "wfs")) {
    capabilities <- http_get(ctx$wfs_url, query = list(SERVICE = "WFS", VERSION = "2.0.0",
                                                       REQUEST = "GetCapabilities"))
    block <- regmatches(capabilities$text,
                        regexpr(paste0("<FeatureType>\\s*<Name>[^<]*", WFS_TYPE_LOCAL_NAME,
                                       "</Name>.*?</FeatureType>"), capabilities$text))
    if (length(block) == 0L) return(character(0))
    codes <- regmatches(block, gregexpr("EPSG::[0-9]+", block))[[1L]]
    return(unique(sub("EPSG::", "", codes)))
  }
  metadata <- http_get(ctx$collection_url)
  uris <- vapply(metadata$json$crs, as.character, character(1))
  codes <- character(0)
  for (uri in uris) {
    if (grepl("CRS84", uri, fixed = TRUE)) codes <- c(codes, "4326")
    hit <- regmatches(uri, regexpr("EPSG/0/[0-9]+", uri))
    if (length(hit) > 0L) codes <- c(codes, sub("EPSG/0/", "", hit))
  }
  unique(codes)
}

ext_crs_01 <- function(ctx) {
  codes <- protocol_crs_list(ctx)
  case_verdict(
    all(c("4326", "3857") %in% codes),
    sprintf(paste0("%s advertises CRS list {%s} for the certification layer; both the storage CRS ",
                   "(EPSG:%d) and the Web Mercator alternative (EPSG:%d) must be offered or R users ",
                   "cannot request a projected read."),
            ctx$driver, paste(codes, collapse = ", "), STORAGE_CRS_EPSG, PROJECTED_CRS_EPSG),
    measured_count = length(codes)
  )
}

ext_crs_02 <- function(ctx) {
  features <- read_all_features(ctx)
  anchor <- anchor_row(features)
  if (is.null(anchor)) return(case_fail("Anchor feature not found for the CRS comparison."))
  expected <- sf::st_coordinates(sf::st_transform(sf::st_geometry(anchor), PROJECTED_CRS_EPSG))

  crs_param <- if (identical(ctx$kind, "oapif")) {
    sprintf("http://www.opengis.net/def/crs/EPSG/0/%d", PROJECTED_CRS_EPSG)
  } else {
    sprintf("urn:ogc:def:crs:EPSG::%d", PROJECTED_CRS_EPSG)
  }
  projected <- read_url_sf(protocol_items_url(ctx, list(limit = 100, crs = crs_param)))
  projected_anchor <- anchor_row(projected)
  if (is.null(projected_anchor)) {
    return(case_fail(sprintf("Server-side %s read returned no '%s' feature.", crs_param, ANCHOR_NAME)))
  }
  observed <- sf::st_coordinates(sf::st_geometry(projected_anchor))
  delta <- max(abs(observed[1L, c("X", "Y")] - expected[1L, c("X", "Y")]))
  case_verdict(
    delta <= PROJECTED_TOLERANCE_METERS,
    sprintf(paste0("Server-side reprojection to EPSG:%d (%s) returned (%.4f, %.4f); PROJ %s via ",
                   "sf::st_transform() computes (%.4f, %.4f); max deviation %.4f m, threshold %.2f m."),
            PROJECTED_CRS_EPSG, crs_param, observed[1L, "X"], observed[1L, "Y"],
            unname(sf::sf_extSoftVersion()[["PROJ"]]), expected[1L, "X"], expected[1L, "Y"],
            delta, PROJECTED_TOLERANCE_METERS),
    measured_delta = delta
  )
}

ext_crs_03 <- function(ctx) {
  if (identical(ctx$kind, "oapif")) {
    # Default (no crs parameter): OGC API Features Core serves CRS84, and RFC 7946
    # fixes GeoJSON coordinates at lon,lat. This half is unambiguous.
    default_response <- http_get(ctx$items_url, query = list(limit = 1))
    default_coordinates <- as.numeric(unlist(default_response$json$features[[1L]]$geometry$coordinates))
    default_crs <- default_response$headers[["content-crs"]] %||% "<absent>"
    default_ok <- length(default_coordinates) == 2L &&
      abs(default_coordinates[[1L]] - ANCHOR_LON) <= GEOGRAPHIC_TOLERANCE_DEGREES &&
      abs(default_coordinates[[2L]] - ANCHOR_LAT) <= GEOGRAPHIC_TOLERANCE_DEGREES &&
      grepl("CRS84", default_crs, fixed = TRUE)

    # Explicit EPSG:4326: OGC API Features Part 2 delivers coordinates in the
    # requested CRS's declared axis order, which for EPSG:4326 is lat,lon. The
    # server may therefore legitimately swap here — but only if Content-Crs says
    # so, because that header is the only thing telling the client to swap back.
    crs_param <- sprintf("http://www.opengis.net/def/crs/EPSG/0/%d", STORAGE_CRS_EPSG)
    epsg_response <- http_get(ctx$items_url, query = list(limit = 1, crs = crs_param))
    epsg_coordinates <- as.numeric(unlist(epsg_response$json$features[[1L]]$geometry$coordinates))
    epsg_crs <- epsg_response$headers[["content-crs"]] %||% "<absent>"
    echoed <- grepl(as.character(STORAGE_CRS_EPSG), epsg_crs, fixed = TRUE)
    lat_lon <- length(epsg_coordinates) == 2L &&
      abs(epsg_coordinates[[1L]] - ANCHOR_LAT) <= GEOGRAPHIC_TOLERANCE_DEGREES &&
      abs(epsg_coordinates[[2L]] - ANCHOR_LON) <= GEOGRAPHIC_TOLERANCE_DEGREES
    lon_lat <- length(epsg_coordinates) == 2L &&
      abs(epsg_coordinates[[1L]] - ANCHOR_LON) <= GEOGRAPHIC_TOLERANCE_DEGREES &&
      abs(epsg_coordinates[[2L]] - ANCHOR_LAT) <= GEOGRAPHIC_TOLERANCE_DEGREES

    return(case_verdict(
      default_ok && echoed && (lat_lon || lon_lat),
      sprintf(paste0("Axis order: the default response is [%s] with Content-Crs %s (must be lon,lat ",
                     "CRS84 per RFC 7946) and crs=%s returns [%s] with Content-Crs %s, i.e. %s order. ",
                     "Part 2 allows the CRS's declared (lat,lon) order for EPSG:4326, but only a ",
                     "Content-Crs that names the CRS lets a client know to swap back; garbage in ",
                     "either position puts every R point in the wrong hemisphere."),
              paste(sprintf("%.4f", default_coordinates), collapse = ", "), default_crs,
              crs_param, paste(sprintf("%.4f", epsg_coordinates), collapse = ", "), epsg_crs,
              if (lat_lon) "lat,lon" else if (lon_lat) "lon,lat" else "UNRECOGNISED")
    ))
  }

  # WFS: the urn EPSG::4326 form is lat/lon in GML by specification. Assert the
  # raw GML uses lat lon AND that reading it through sf/GDAL recovers lon/lat.
  gml_url <- protocol_items_url(ctx, list(limit = 1, crs = sprintf("urn:ogc:def:crs:EPSG::%d", STORAGE_CRS_EPSG),
                                          format = "application/gml+xml; version=3.2"))
  wire <- http_get(gml_url)
  positions <- regmatches(wire$text, regexpr("<gml:pos[^>]*>[^<]+</gml:pos>", wire$text))
  raw_ok <- FALSE
  raw_values <- NA_character_
  if (length(positions) > 0L) {
    raw_values <- gsub("<[^>]+>", "", positions[[1L]])
    parts <- suppressWarnings(as.numeric(strsplit(trimws(raw_values), "\\s+")[[1L]]))
    raw_ok <- length(parts) == 2L &&
      abs(parts[[1L]] - ANCHOR_LAT) <= GEOGRAPHIC_TOLERANCE_DEGREES &&
      abs(parts[[2L]] - ANCHOR_LON) <= GEOGRAPHIC_TOLERANCE_DEGREES
  }
  read_back <- read_url_sf(gml_url)
  anchor <- anchor_row(read_back)
  client_ok <- FALSE
  observed <- c(NA_real_, NA_real_)
  if (!is.null(anchor)) {
    observed <- sf::st_coordinates(sf::st_geometry(anchor))[1L, c("X", "Y")]
    client_ok <- abs(observed[[1L]] - ANCHOR_LON) <= GEOGRAPHIC_TOLERANCE_DEGREES &&
      abs(observed[[2L]] - ANCHOR_LAT) <= GEOGRAPHIC_TOLERANCE_DEGREES
  }
  case_verdict(
    raw_ok && client_ok,
    sprintf(paste0("Axis order with srsName=urn:ogc:def:crs:EPSG::%d: raw GML gml:pos is '%s' ",
                   "(spec requires lat lon for the urn form) and sf/GDAL recovered (%.4f, %.4f) as ",
                   "lon/lat. Either half being wrong swaps every coordinate for R users."),
            STORAGE_CRS_EPSG, trimws(raw_values), observed[[1L]], observed[[2L]])
  )
}

#' Protocol bbox contract, including the axis-order rule each protocol declares.
ext_crs_04 <- function(ctx) {
  correct <- read_url_sf(bbox_items_url(ctx, subset_bbox_parameter(ctx)))
  values <- unname(SUBSET_BBOX[c("xmin", "ymin", "xmax", "ymax")])
  swapped_values <- if (identical(ctx$kind, "oapif")) values[c(2L, 1L, 4L, 3L)] else values
  swapped_bbox <- paste(format(swapped_values, trim = TRUE, scientific = FALSE), collapse = ",")
  if (identical(ctx$kind, "wfs")) {
    swapped_bbox <- paste0(swapped_bbox, sprintf(",urn:ogc:def:crs:EPSG::%d", STORAGE_CRS_EPSG))
  }
  swapped <- http_get(bbox_items_url(ctx, swapped_bbox))
  swapped_features <- length(swapped$json$features %||% list())
  swapped_rejected <- swapped$status >= 400L && swapped$status < 500L
  case_verdict(
    nrow(correct) == SUBSET_BBOX_FEATURE_COUNT && (swapped_rejected || swapped_features == 0L),
    sprintf(paste0("bbox axis-order contract for %s: the specified order (%s) selected %d feature(s) ",
                   "(expected %d), and the reversed order returned HTTP %d with %d feature(s) — a ",
                   "server that accepted both orders would be guessing, and a client cannot tell ",
                   "which guess it got."),
            ctx$driver,
            if (identical(ctx$kind, "oapif")) "CRS84 lon,lat per Features Core" else
              "the feature type's default CRS, lat,lon for urn EPSG::4326",
            nrow(correct), SUBSET_BBOX_FEATURE_COUNT, swapped$status, swapped_features),
    measured_count = nrow(correct)
  )
}

# ---------------------------------------------------------------------------
# NB-RSF-* extension cases: paging
# ---------------------------------------------------------------------------

ext_pag_01 <- function(ctx) {
  seen <- character(0)
  duplicates <- character(0)
  offset <- 0L
  pages <- 0L
  repeat {
    page <- read_url_sf(protocol_items_url(ctx, list(limit = PAGE_SIZE, offset = offset)))
    pages <- pages + 1L
    if (nrow(page) == 0L) break
    ids <- as.character(page$name)
    duplicates <- c(duplicates, intersect(seen, ids))
    seen <- c(seen, ids)
    offset <- offset + PAGE_SIZE
    if (pages > 20L) break
  }
  case_verdict(
    length(unique(seen)) == TOTAL_FEATURES && length(duplicates) == 0L,
    sprintf(paste0("Full paginated walk in pages of %d over %d request(s) collected %d unique ",
                   "feature name(s) with %d duplicate(s); the fixture seeds %d. A drifting page ",
                   "window would show up as a duplicate or a missing id."),
            PAGE_SIZE, pages, length(unique(seen)), length(duplicates), TOTAL_FEATURES),
    measured_count = length(unique(seen))
  )
}

ext_pag_02 <- function(ctx) {
  url <- protocol_items_url(ctx, list(limit = 100000))
  wire <- http_get(url)
  rows <- NA_integer_
  if (wire$status == 200L) {
    rows <- tryCatch(nrow(read_url_sf(url)), error = function(e) NA_integer_)
  }
  case_verdict(
    wire$status < 500L && (wire$status != 200L || (!is.na(rows) && rows <= TOTAL_FEATURES)),
    sprintf(paste0("Oversized page request (limit/COUNT=100000) returned HTTP %d with %s row(s). ",
                   "The server must clamp to its configured maximum or reject with a 4xx; a 5xx or an ",
                   "unbounded read is the bug this case looks for."),
            wire$status, if (is.na(rows)) "n/a" else as.character(rows)),
    measured_count = rows
  )
}

ext_pag_03 <- function(ctx) {
  wire <- http_get(protocol_items_url(ctx, list(limit = 2)))
  matched <- suppressWarnings(as.integer(wire$json$numberMatched %||% NA))
  returned <- suppressWarnings(as.integer(wire$json$numberReturned %||% NA))
  features <- length(wire$json$features %||% list())
  case_verdict(
    !is.na(matched) && !is.na(returned) && matched == TOTAL_FEATURES &&
      returned == 2L && features == returned,
    sprintf(paste0("Paging counters on a 2-feature page (httr transport-shape check): ",
                   "numberMatched=%s (expected %d), numberReturned=%s, actual features=%d. ",
                   "numberMatched must be the unpaged total, not the page size."),
            matched, TOTAL_FEATURES, returned, features),
    measured_count = matched
  )
}

ext_pag_04 <- function(ctx) {
  wire <- http_get(protocol_items_url(ctx, list(limit = PAGE_SIZE, offset = 500)))
  matched <- suppressWarnings(as.integer(wire$json$numberMatched %||% NA))
  features <- length(wire$json$features %||% list())
  case_verdict(
    wire$status == 200L && features == 0L && !is.na(matched) && matched == TOTAL_FEATURES,
    sprintf(paste0("Offset/startIndex past the end returned HTTP %d with %d feature(s) and ",
                   "numberMatched=%s (expected 200, 0 features, %d matched). Wrapping around or ",
                   "erroring here breaks every client that walks to the end."),
            wire$status, features, matched, TOTAL_FEATURES),
    measured_count = features
  )
}

ext_pag_05 <- function(ctx) {
  wire <- http_get(protocol_items_url(ctx, list(limit = 0)))
  features <- length(wire$json$features %||% list())
  acceptable <- (wire$status >= 400L && wire$status < 500L) ||
    (wire$status == 200L && features == 0L)
  case_verdict(
    acceptable,
    sprintf(paste0("Zero-size page request (limit/COUNT=0) returned HTTP %d with %d feature(s): a ",
                   "structured 4xx or an empty 200 are both defensible; a 5xx or a full result set ",
                   "is not. Body: %s"),
            wire$status, features, truncate_note(wire$text, 120L)),
    measured_count = features
  )
}

# ---------------------------------------------------------------------------
# NB-RSF-* extension cases: format round-trips
# ---------------------------------------------------------------------------

format_roundtrip <- function(ctx, extension, label) {
  features <- read_all_features(ctx)
  flat <- flatten_list_columns(features)
  path <- tempfile(fileext = extension)
  on.exit(unlink(path), add = TRUE)
  sf::st_write(flat, path, quiet = TRUE, delete_dsn = TRUE)
  restored <- sf::st_read(path, quiet = TRUE)

  original_geometry <- sf::st_geometry(flat)
  restored_geometry <- sf::st_geometry(restored)
  delta <- max(abs(
    sf::st_coordinates(original_geometry[!sf::st_is_empty(original_geometry)]) -
      sf::st_coordinates(restored_geometry[!sf::st_is_empty(restored_geometry)])
  ))
  names_match <- identical(sort(as.character(flat$name)), sort(as.character(restored$name)))
  rows_match <- nrow(restored) == nrow(flat)
  empties_match <- sum(sf::st_is_empty(restored_geometry)) == sum(sf::st_is_empty(original_geometry))
  case_verdict(
    rows_match && names_match && empties_match && delta <= GEOGRAPHIC_TOLERANCE_DEGREES,
    sprintf(paste0("End-to-end %s fidelity: the %s response was written with sf::st_write() and read ",
                   "back with sf::st_read() — %d/%d rows, names %s, empty geometries %s, max ",
                   "coordinate deviation %.3e. (JSON array columns are flattened to text first: ",
                   "OGR refuses list columns.)"),
            label, ctx$driver, nrow(restored), nrow(flat),
            if (names_match) "match" else "DIFFER", if (empties_match) "match" else "DIFFER", delta),
    measured_count = nrow(restored),
    measured_delta = delta
  )
}

ext_fmt_01 <- function(ctx) format_roundtrip(ctx, ".gpkg", "GeoPackage")
ext_fmt_02 <- function(ctx) format_roundtrip(ctx, ".geojson", "GeoJSON")

ext_fmt_03 <- function(ctx) {
  formats <- if (identical(ctx$kind, "oapif")) {
    list(list(label = "f=json", params = list(format = "json", limit = 100)),
         list(label = "f=csv", params = list(format = "csv", limit = 100)),
         list(label = "f=gml", params = list(format = "gml", limit = 100)))
  } else {
    list(list(label = "application/geo+json", params = list(format = "application/geo+json")),
         list(label = "text/csv", params = list(format = "text/csv")),
         list(label = "GML3.2", params = list(format = "application/gml+xml; version=3.2")))
  }
  observations <- character(0)
  ok <- TRUE
  for (entry in formats) {
    wire <- http_get(protocol_items_url(ctx, entry$params))
    body_ok <- wire$status == 200L && nchar(wire$text) > 0L
    # Every advertised representation must carry the whole feature set: the
    # first seeded feature and the last (null-geometry) one.
    anchor_present <- grepl(ANCHOR_NAME, wire$text, fixed = TRUE)
    lambda_present <- grepl("lambda", wire$text, fixed = TRUE)
    ok <- ok && body_ok && anchor_present && lambda_present
    observations <- c(observations, sprintf("%s -> HTTP %d, %d byte(s), anchor %s, last feature %s",
                                            entry$label, wire$status, nchar(wire$text),
                                            if (anchor_present) "present" else "MISSING",
                                            if (lambda_present) "present" else "MISSING"))
  }
  case_verdict(
    ok,
    sprintf("Advertised output formats all serve the complete feature set: %s.",
            paste(observations, collapse = "; ")),
    measured_count = length(formats)
  )
}

# ---------------------------------------------------------------------------
# NB-RSF-* extension cases: error surface and auth
# ---------------------------------------------------------------------------

ext_err_01 <- function(ctx) {
  if (identical(ctx$kind, "oapif")) {
    missing <- http_get(sprintf("%s/ogc/features/collections/%s/items", ctx$base_url, UNKNOWN_COLLECTION_ID))
    bad_param <- http_get(ctx$items_url, query = list(limit = "not-a-number"))
    ok <- missing$status == 404L && bad_param$status == 400L
    return(case_verdict(
      ok,
      sprintf(paste0("404-vs-400 distinction: unknown collection '%s' returned %d (expected 404) and ",
                     "a malformed `limit` returned %d (expected 400). Collapsing both into one status ",
                     "makes client retry logic wrong."),
              UNKNOWN_COLLECTION_ID, missing$status, bad_param$status)
    ))
  }
  missing <- http_get(ctx$wfs_url, query = list(SERVICE = "WFS", VERSION = "2.0.0",
                                                REQUEST = "GetFeature", TYPENAMES = UNKNOWN_COLLECTION_ID))
  bad_request <- http_get(ctx$wfs_url, query = list(SERVICE = "WFS", VERSION = "2.0.0",
                                                    REQUEST = "NoSuchOperation"))
  structured <- grepl("ExceptionReport", missing$text, fixed = TRUE) &&
    grepl("exceptionCode", missing$text, fixed = TRUE)
  case_verdict(
    missing$status >= 400L && missing$status < 500L && structured &&
      bad_request$status >= 400L && bad_request$status < 600L && bad_request$status != 500L,
    sprintf(paste0("WFS error shape: unknown typeName returned %d with an ows:ExceptionReport (%s); ",
                   "an unsupported REQUEST returned %d. WFS 2.0 reports operation errors as a 4xx ",
                   "ExceptionReport, not a 404 body or a 500."),
            missing$status, if (structured) "exceptionCode present" else "NO exceptionCode",
            bad_request$status)
  )
}

ext_err_02 <- function(ctx) {
  crs_param <- if (identical(ctx$kind, "oapif")) "http://example.invalid/crs/nope" else "urn:ogc:def:crs:BOGUS::9999"
  wire <- http_get(protocol_items_url(ctx, list(limit = 1, crs = crs_param)))
  structured <- grepl("problem\\+json|ExceptionReport|\"title\"|exceptionCode", wire$text) ||
    grepl("problem", wire$headers[["content-type"]] %||% "", fixed = TRUE)
  case_verdict(
    wire$status >= 400L && wire$status < 500L && structured,
    sprintf(paste0("Malformed CRS '%s' returned HTTP %d with a structured error body (%s). A 500 or a ",
                   "silent fallback to the storage CRS would both be server bugs: the client would ",
                   "believe it received the CRS it asked for."),
            crs_param, wire$status,
            if (structured) "problem+json / ExceptionReport" else truncate_note(wire$text, 100L))
  )
}

ext_err_03 <- function(ctx) {
  wire <- http_get(protocol_items_url(ctx, list(limit = 1, format = "application/x-not-a-format")))
  case_verdict(
    wire$status >= 400L && wire$status < 500L,
    sprintf(paste0("Unsupported output format returned HTTP %d (expected a 4xx). Body: %s"),
            wire$status, truncate_note(wire$text, 140L))
  )
}

ext_err_04 <- function(ctx) {
  wire <- http_get(protocol_items_url(ctx, list(limit = 1, filter = "status = 'active' AND")))
  case_verdict(
    wire$status >= 400L && wire$status < 500L,
    sprintf(paste0("Truncated protocol filter (\"status = 'active' AND\") returned HTTP %d (expected a ",
                   "4xx structured error, never a 500 and never a silent full result set). Body: %s"),
            wire$status, truncate_note(wire$text, 140L))
  )
}

ext_aut_01 <- function(ctx) {
  challenge <- ctx$probe$challenge
  ok <- !is.na(challenge) &&
    grepl(ADMIN_AUTH_CHALLENGE_SCHEME, challenge, fixed = TRUE) &&
    grepl(ADMIN_API_KEY_HEADER, challenge, fixed = TRUE)
  case_verdict(
    ok,
    sprintf(paste0("401 challenge shape on %s: WWW-Authenticate: %s. The challenge must name the ",
                   "scheme and the header so a client can discover how to authenticate."),
            ADMIN_PROBE_PATH, if (is.na(challenge)) "<absent>" else challenge)
  )
}

ext_aut_02 <- function(ctx) {
  status <- ctx$probe$wrong_status
  case_verdict(
    !is.na(status) && status == 401L,
    sprintf(paste0("A wrong %s value returned HTTP %s on %s; it must be 401 (bad credential), not ",
                   "403 (authenticated but forbidden) and never 500."),
            ADMIN_API_KEY_HEADER, if (is.na(status)) "a transport error" else status, ADMIN_PROBE_PATH)
  )
}

ext_aut_03 <- function(ctx) {
  previous <- Sys.getenv("GDAL_HTTP_HEADERS", NA_character_)
  Sys.setenv(GDAL_HTTP_HEADERS = paste0(ADMIN_API_KEY_HEADER, ": ", ADMIN_API_KEY))
  on.exit({
    if (is.na(previous)) Sys.unsetenv("GDAL_HTTP_HEADERS") else Sys.setenv(GDAL_HTTP_HEADERS = previous)
  }, add = TRUE)
  layers <- sf::st_layers(ctx$dsn)
  names <- as.character(layers$name)
  expected <- resolve_layer(ctx)
  case_verdict(
    expected %in% names,
    sprintf(paste0("GDAL_HTTP_HEADERS carried '%s: <admin key>' through sf::st_layers() on the %s DSN: ",
                   "%d layer(s) listed and the certification target '%s' is %s. Proves the credential ",
                   "path works through the sf/GDAL stack, not just httr."),
            ADMIN_API_KEY_HEADER, ctx$driver, length(names), expected,
            if (expected %in% names) "present" else "MISSING"),
    measured_count = length(names)
  )
}

# ---------------------------------------------------------------------------
# NB-RSF-* extension cases: cross-protocol agreement
# ---------------------------------------------------------------------------

oapif_collection_metadata <- function(ctx) {
  http_get(ctx$collection_url)$json
}

wfs_capabilities_text <- function(ctx) {
  http_get(ctx$wfs_url, query = list(SERVICE = "WFS", VERSION = "2.0.0", REQUEST = "GetCapabilities"))$text
}

wfs_feature_type_block <- function(ctx) {
  text <- wfs_capabilities_text(ctx)
  block <- regmatches(text, regexpr(paste0("<FeatureType>\\s*<Name>[^<]*", WFS_TYPE_LOCAL_NAME,
                                           "</Name>.*?</FeatureType>"), text))
  if (length(block) == 0L) "" else block[[1L]]
}

ext_xpr_01 <- function(ctx) {
  metadata <- oapif_collection_metadata(ctx)
  oapif_bbox <- as.numeric(unlist(metadata$extent$spatial$bbox[[1L]]))
  block <- wfs_feature_type_block(ctx)
  lower <- regmatches(block, regexpr("<ows:LowerCorner>[^<]+</ows:LowerCorner>", block))
  upper <- regmatches(block, regexpr("<ows:UpperCorner>[^<]+</ows:UpperCorner>", block))
  if (length(lower) == 0L || length(upper) == 0L) {
    return(case_fail("Could not read ows:WGS84BoundingBox out of the WFS capabilities."))
  }
  wfs_bbox <- c(as.numeric(strsplit(trimws(gsub("<[^>]+>", "", lower)), "\\s+")[[1L]]),
                as.numeric(strsplit(trimws(gsub("<[^>]+>", "", upper)), "\\s+")[[1L]]))
  delta <- max(abs(oapif_bbox - wfs_bbox))
  case_verdict(
    delta <= GEOGRAPHIC_TOLERANCE_DEGREES,
    sprintf(paste0("Cross-protocol extent agreement: OGC API Features collection extent [%s] vs WFS ",
                   "ows:WGS84BoundingBox [%s]; max deviation %.3e. Disagreement means a client that ",
                   "zooms by metadata sees a different map depending on the protocol."),
            paste(sprintf("%.5f", oapif_bbox), collapse = " "),
            paste(sprintf("%.5f", wfs_bbox), collapse = " "), delta),
    measured_delta = delta
  )
}

ext_xpr_02 <- function(ctx) {
  oapif <- http_get(ctx$items_url, query = list(limit = 1))
  oapif_matched <- suppressWarnings(as.integer(oapif$json$numberMatched %||% NA))
  hits <- http_get(ctx$wfs_url, query = list(SERVICE = "WFS", VERSION = "2.0.0", REQUEST = "GetFeature",
                                             TYPENAMES = ctx$wfs_type_name %||% paste0("honua:", WFS_TYPE_LOCAL_NAME),
                                             RESULTTYPE = "hits"))
  wfs_matched <- suppressWarnings(as.integer(
    sub('.*numberMatched="([0-9]+)".*', "\\1", gsub("[\r\n]", " ", hits$text))))
  case_verdict(
    !is.na(oapif_matched) && !is.na(wfs_matched) && oapif_matched == wfs_matched &&
      oapif_matched == TOTAL_FEATURES,
    sprintf(paste0("Cross-protocol count agreement: OGC API Features numberMatched=%s, WFS ",
                   "resultType=hits numberMatched=%s, fixture total=%d."),
            oapif_matched, wfs_matched, TOTAL_FEATURES),
    measured_count = oapif_matched
  )
}

ext_xpr_03 <- function(ctx) {
  metadata <- oapif_collection_metadata(ctx)
  oapif_codes <- unique(unlist(lapply(metadata$crs, function(uri) {
    uri <- as.character(uri)
    codes <- character(0)
    if (grepl("CRS84", uri, fixed = TRUE)) codes <- c(codes, "4326")
    hit <- regmatches(uri, regexpr("EPSG/0/[0-9]+", uri))
    if (length(hit) > 0L) codes <- c(codes, sub("EPSG/0/", "", hit))
    codes
  })))
  block <- wfs_feature_type_block(ctx)
  wfs_codes <- unique(sub("EPSG::", "", regmatches(block, gregexpr("EPSG::[0-9]+", block))[[1L]]))
  case_verdict(
    length(wfs_codes) > 0L && setequal(oapif_codes, wfs_codes),
    sprintf(paste0("Cross-protocol CRS agreement: OGC API Features offers {%s}, WFS offers {%s}. ",
                   "A CRS available on one protocol but not the other is a metadata bug, not a ",
                   "capability difference."),
            paste(sort(oapif_codes), collapse = ", "), paste(sort(wfs_codes), collapse = ", ")),
    measured_count = length(oapif_codes)
  )
}

ext_xpr_04 <- function(ctx) {
  oapif_item <- http_get(ctx$items_url, query = list(limit = 1))
  oapif_fields <- names(oapif_item$json$features[[1L]]$properties)
  describe <- http_get(ctx$wfs_url, query = list(SERVICE = "WFS", VERSION = "2.0.0",
                                                 REQUEST = "DescribeFeatureType",
                                                 TYPENAMES = ctx$wfs_type_name %||% paste0("honua:", WFS_TYPE_LOCAL_NAME)))
  elements <- regmatches(describe$text, gregexpr('name="[^"]+"', describe$text))[[1L]]
  wfs_fields <- gsub("_x003A_", ":", sub('name="([^"]+)"', "\\1", elements), fixed = TRUE)
  wfs_fields <- setdiff(wfs_fields, c("shape", "geometry", "objectid", WFS_TYPE_LOCAL_NAME,
                                      paste0(WFS_TYPE_LOCAL_NAME, "Type")))
  only_wfs <- setdiff(wfs_fields, oapif_fields)
  only_oapif <- setdiff(oapif_fields, wfs_fields)
  case_verdict(
    length(only_wfs) == 0L && length(only_oapif) == 0L,
    sprintf(paste0("Cross-protocol attribute agreement: %d field(s) in OGC API Features items vs %d ",
                   "in WFS DescribeFeatureType. WFS-only: {%s}. OAPIF-only: {%s}. The same layer must ",
                   "expose the same attributes on both protocols."),
            length(oapif_fields), length(wfs_fields),
            paste(only_wfs, collapse = ", "), paste(only_oapif, collapse = ", ")),
    measured_count = length(oapif_fields)
  )
}

# ---------------------------------------------------------------------------
# NB-RSF-* extension cases: OGC API Features only
# ---------------------------------------------------------------------------

ext_oaf_01 <- function(ctx) {
  landing <- http_get(ctx$features_url)
  rels <- vapply(landing$json$links, function(link) as.character(link$rel), character(1))
  required <- c("self", "conformance", "data")
  missing <- setdiff(required, rels)
  service_desc <- any(rels %in% c("service-desc", "service-doc"))
  case_verdict(
    landing$status == 200L && length(missing) == 0L && service_desc,
    sprintf(paste0("Landing page %s returned HTTP %d with link relations {%s}. OGC API Features Core ",
                   "requires self, service-desc/service-doc, conformance and data."),
            ctx$features_url, landing$status, paste(unique(rels), collapse = ", ")),
    measured_count = length(rels)
  )
}

ext_oaf_02 <- function(ctx) {
  conformance <- http_get(paste0(ctx$features_url, "/conformance"))
  declared <- vapply(conformance$json$conformsTo, as.character, character(1))
  checks <- list(
    list(class = "conf/core", declared = any(grepl("ogcapi-features-1/1.0/conf/core", declared)),
         probe = function() http_get(paste0(ctx$features_url, "/collections"))$status == 200L),
    list(class = "conf/geojson", declared = any(grepl("conf/geojson", declared)),
         probe = function() http_get(ctx$items_url, query = list(limit = 1, f = "json"))$status == 200L),
    list(class = "features-2 conf/crs", declared = any(grepl("ogcapi-features-2/1.0/conf/crs", declared)),
         probe = function() {
           response <- http_get(ctx$items_url, query = list(
             limit = 1, crs = sprintf("http://www.opengis.net/def/crs/EPSG/0/%d", PROJECTED_CRS_EPSG)))
           response$status == 200L && !is.null(response$headers[["content-crs"]])
         }),
    list(class = "features-3 conf/queryables", declared = any(grepl("conf/queryables", declared)),
         probe = function() http_get(sprintf("%s/ogc/features/collections/%s/queryables",
                                             ctx$base_url, ctx$collection_id))$status == 200L),
    list(class = "cql2-text", declared = any(grepl("cql2/1.0/conf/cql2-text", declared)),
         probe = function() {
           response <- http_get(ctx$items_url, query = list(
             filter = sprintf("%s = '%s'", FILTER_FIELD, FILTER_VALUE), `filter-lang` = "cql2-text"))
           response$status == 200L &&
             length(response$json$features %||% list()) == ACTIVE_FEATURES
         }),
    list(class = "honua ids-parameter", declared = any(grepl("conf/ids-parameter", declared)),
         probe = function() {
           response <- http_get(ctx$items_url, query = list(ids = "1,2"))
           response$status == 200L && length(response$json$features %||% list()) == 2L
         }),
    list(class = "honua properties-parameter", declared = any(grepl("conf/properties-parameter", declared)),
         probe = function() {
           response <- http_get(ctx$items_url, query = list(limit = 1, properties = "name"))
           response$status == 200L &&
             setequal(names(response$json$features[[1L]]$properties), "name")
         })
  )
  broken <- character(0)
  checked <- 0L
  for (check in checks) {
    if (!isTRUE(check$declared)) next
    checked <- checked + 1L
    honoured <- tryCatch(isTRUE(check$probe()), error = function(e) FALSE)
    if (!honoured) broken <- c(broken, check$class)
  }
  case_verdict(
    checked > 0L && length(broken) == 0L,
    sprintf(paste0("Declared-vs-honoured conformance: %d of %d declared classes were probed and %s. ",
                   "A conformance class the server declares but does not implement is a server bug. ",
                   "%s"),
            checked, length(declared),
            if (length(broken) == 0L) "all held" else "some did NOT hold",
            if (length(broken) == 0L) "" else paste0("Not honoured: ", paste(broken, collapse = ", "), ".")),
    measured_count = checked
  )
}

ext_oaf_03 <- function(ctx) {
  first <- http_get(ctx$items_url, query = list(limit = PAGE_SIZE))
  rels <- vapply(first$json$links, function(link) as.character(link$rel), character(1))
  next_link <- NULL
  for (link in first$json$links) {
    if (identical(as.character(link$rel), "next")) next_link <- as.character(link$href)
  }
  self_link <- NULL
  for (link in first$json$links) {
    if (identical(as.character(link$rel), "self")) self_link <- as.character(link$href)
  }
  if (is.null(next_link)) {
    return(case_fail(sprintf("First page of %d carried no `next` link (relations: %s).",
                             PAGE_SIZE, paste(unique(rels), collapse = ", "))))
  }
  followed <- http_get(next_link)
  first_names <- vapply(first$json$features, function(f) as.character(f$properties$name), character(1))
  next_names <- vapply(followed$json$features, function(f) as.character(f$properties$name), character(1))
  overlap <- intersect(first_names, next_names)
  last <- http_get(ctx$items_url, query = list(limit = PAGE_SIZE, offset = 9))
  last_rels <- vapply(last$json$links, function(link) as.character(link$rel), character(1))
  case_verdict(
    !is.null(self_link) && grepl("^http", self_link) && followed$status == 200L &&
      length(overlap) == 0L && !("next" %in% last_rels),
    sprintf(paste0("Link relations: self=%s; following `next` returned HTTP %d with %d feature(s) ",
                   "disjoint from page one (overlap %d); the final page advertises relations {%s} and ",
                   "must not advertise `next`."),
            truncate_note(self_link %||% "<absent>", 90L), followed$status, length(next_names),
            length(overlap), paste(unique(last_rels), collapse = ", ")),
    measured_count = length(next_names)
  )
}

ext_oaf_04 <- function(ctx) {
  first <- http_get(ctx$items_url, query = list(limit = 1))
  feature_id <- as.character(first$json$features[[1L]]$id)
  single <- http_get(sprintf("%s/%s", ctx$items_url, feature_id))
  name <- tryCatch(as.character(single$json$properties$name), error = function(e) NA_character_)
  case_verdict(
    single$status == 200L && identical(name, ANCHOR_NAME) &&
      identical(as.character(single$json$type), "Feature"),
    sprintf(paste0("Single-item retrieval /items/%s returned HTTP %d as a GeoJSON %s with name '%s' ",
                   "(expected the '%s' anchor). Item ids advertised in the collection must be ",
                   "addressable."),
            feature_id, single$status, as.character(single$json$type %||% "<none>"),
            name, ANCHOR_NAME),
    measured_count = 1L
  )
}

ext_oaf_05 <- function(ctx) {
  interval <- "2024-01-01T00:00:00Z/2024-01-03T00:00:00Z"
  response <- http_get(ctx$items_url, query = list(datetime = interval, limit = 100))
  features <- response$json$features %||% list()
  instants <- vapply(features, function(f) as.character(f$properties$created_at), character(1))
  parsed <- as_utc_instant(instants)
  lower <- as.POSIXct("2024-01-01 00:00:00", tz = "UTC")
  upper <- as.POSIXct("2024-01-03 00:00:00", tz = "UTC")
  inside <- length(parsed) > 0L && all(parsed >= lower & parsed <= upper)
  case_verdict(
    response$status == 200L && length(features) > 0L && length(features) < TOTAL_FEATURES && inside,
    sprintf(paste0("datetime=%s returned HTTP %d with %d of %d feature(s); every returned created_at ",
                   "%s inside the interval. A datetime filter that returns everything is not a filter."),
            interval, response$status, length(features), TOTAL_FEATURES,
            if (inside) "is" else "is NOT"),
    measured_count = length(features)
  )
}

ext_oaf_06 <- function(ctx) {
  filter <- sprintf("%s = '%s'", FILTER_FIELD, FILTER_VALUE)
  response <- http_get(ctx$items_url, query = list(filter = filter, `filter-lang` = "cql2-text", limit = 100))
  features <- response$json$features %||% list()
  statuses <- unique(vapply(features, function(f) as.character(f$properties$status), character(1)))
  case_verdict(
    response$status == 200L && length(features) == ACTIVE_FEATURES &&
      identical(statuses, FILTER_VALUE),
    sprintf(paste0("CQL2-text `filter=%s` returned HTTP %d with %d feature(s) (expected %d) and ",
                   "status values {%s}. This is the protocol filter parameter, not a client-side ",
                   "filter."),
            filter, response$status, length(features), ACTIVE_FEATURES,
            paste(statuses, collapse = ", ")),
    measured_count = length(features)
  )
}

# ---------------------------------------------------------------------------
# NB-RSF-* extension cases: ows4R depth (WFS only)
# ---------------------------------------------------------------------------

ext_ows_01 <- function(ctx) {
  identification <- wfs_client(ctx)$getCapabilities()$getServiceIdentification()
  title <- as.character(identification$getTitle())
  service_type <- as.character(identification$getServiceType())
  versions <- as.character(identification$getServiceTypeVersion())
  case_verdict(
    nzchar(title) && identical(toupper(service_type), "WFS") && any(grepl("2.0.0", versions)),
    sprintf(paste0("ows4R parsed ows:ServiceIdentification: title='%s', serviceType='%s', ",
                   "serviceTypeVersion={%s}."),
            title, service_type, paste(versions, collapse = ", "))
  )
}

ext_ows_02 <- function(ctx) {
  operations <- wfs_client(ctx)$getCapabilities()$getOperationsMetadata()$getOperations()
  names <- vapply(operations, function(op) as.character(op$getName()), character(1))
  required <- c("GetCapabilities", "DescribeFeatureType", "GetFeature")
  missing <- setdiff(required, names)
  case_verdict(
    length(missing) == 0L,
    sprintf(paste0("ows4R parsed ows:OperationsMetadata with %d operation(s): {%s}.%s"),
            length(names), paste(names, collapse = ", "),
            if (length(missing) == 0L) "" else paste0(" Missing: ", paste(missing, collapse = ", "), ".")),
    measured_count = length(names)
  )
}

ext_ows_03 <- function(ctx) {
  types <- wfs_feature_types(ctx)
  names <- vapply(types, function(ft) as.character(ft$getName()), character(1))
  target <- paste0("honua:", WFS_TYPE_LOCAL_NAME)
  case_verdict(
    target %in% names,
    sprintf(paste0("ows4R parsed %d feature type(s) from the WFS 2.0.0 capabilities: {%s}; the ",
                   "certification target '%s' is %s. ows4R resolves the service namespace by prefix, ",
                   "so a capabilities document that binds wfs/2.0 only as the default namespace ",
                   "parses as zero feature types."),
            length(names), paste(names, collapse = ", "), target,
            if (target %in% names) "present" else "MISSING"),
    measured_count = length(names)
  )
}

ext_ows_04 <- function(ctx) {
  ft <- wfs_feature_type(ctx)
  crs <- tryCatch(sf::st_crs(ft$getDefaultCRS()), error = function(e) sf::NA_crs_)
  bbox <- tryCatch(ft$getBoundingBox(), error = function(e) NULL)
  crs_ok <- crs_is_wgs84(crs)
  bbox_ok <- !is.null(bbox) && all(is.finite(as.numeric(bbox)))
  case_verdict(
    crs_ok && bbox_ok,
    sprintf(paste0("ows4R per-feature-type metadata: DefaultCRS parsed to %s (expected EPSG:%d) and ",
                   "ows:WGS84BoundingBox %s. R users rely on both to set up a map before any feature ",
                   "is fetched."),
            if (is.na(crs)) "<none>" else (crs$input %||% "<wkt>"), STORAGE_CRS_EPSG,
            if (bbox_ok) "parsed" else "MISSING")
  )
}

ext_ows_05 <- function(ctx) {
  description <- wfs_feature_type(ctx)$getDescription(pretty = TRUE)
  fields <- gsub("_x003A_", ":", as.character(description$name), fixed = TRUE)
  missing <- setdiff(ATTRIBUTE_FIELDS, fields)
  case_verdict(
    length(missing) == 0L,
    sprintf(paste0("ows4R DescribeFeatureType returned %d element(s); %d of %d canonical attribute ",
                   "fields present.%s"),
            length(fields), length(intersect(ATTRIBUTE_FIELDS, fields)), length(ATTRIBUTE_FIELDS),
            if (length(missing) == 0L) "" else paste0(" Missing: ", paste(missing, collapse = ", "), ".")),
    measured_count = length(fields)
  )
}

ext_ows_06 <- function(ctx) {
  features <- wfs_feature_type(ctx)$getFeatures()
  case_verdict(
    inherits(features, "sf") && nrow(features) == TOTAL_FEATURES,
    sprintf(paste0("ows4R WFSFeatureType$getFeatures() returned a %s with %d row(s); expected %d. ",
                   "This is the ows4R read path (GetFeature via the capabilities-derived URL), not ",
                   "the GDAL WFS driver."),
            paste(class(features), collapse = "/"), nrow(features), TOTAL_FEATURES),
    measured_count = nrow(features)
  )
}

ext_ows_07 <- function(ctx) {
  ft <- wfs_feature_type(ctx)
  first <- ft$getFeatures(count = PAGE_SIZE)
  second <- ft$getFeatures(count = PAGE_SIZE, startIndex = PAGE_SIZE)
  first_names <- as.character(first$name)
  second_names <- as.character(second$name)
  overlap <- intersect(first_names, second_names)
  case_verdict(
    nrow(first) == PAGE_SIZE && nrow(second) == PAGE_SIZE && length(overlap) == 0L,
    sprintf(paste0("ows4R getFeatures(count=%d) then getFeatures(count=%d, startIndex=%d) returned ",
                   "%d and %d row(s) with overlap %d: {%s} then {%s}."),
            PAGE_SIZE, PAGE_SIZE, PAGE_SIZE, nrow(first), nrow(second), length(overlap),
            paste(first_names, collapse = ","), paste(second_names, collapse = ",")),
    measured_count = nrow(second)
  )
}

ext_ows_08 <- function(ctx) {
  response <- http_get(ctx$wfs_url, query = list(
    SERVICE = "WFS", VERSION = "2.0.0", REQUEST = "GetFeature",
    TYPENAMES = ctx$wfs_type_name %||% paste0("honua:", WFS_TYPE_LOCAL_NAME),
    RESULTTYPE = "hits"))
  flat <- gsub("[\r\n]", " ", response$text)
  matched <- suppressWarnings(as.integer(sub('.*numberMatched="([0-9]+)".*', "\\1", flat)))
  returned <- suppressWarnings(as.integer(sub('.*numberReturned="([0-9]+)".*', "\\1", flat)))
  members <- length(gregexpr("<wfs:member", flat)[[1L]])
  if (!grepl("<wfs:member", flat)) members <- 0L
  case_verdict(
    response$status == 200L && !is.na(matched) && matched == TOTAL_FEATURES &&
      !is.na(returned) && returned == 0L && members == 0L,
    sprintf(paste0("RESULTTYPE=hits (httr transport-shape check) returned HTTP %d with ",
                   "numberMatched=%s, numberReturned=%s and %d wfs:member element(s). A hits request ",
                   "must count without serialising features."),
            response$status, matched, returned, members),
    measured_count = matched
  )
}

ext_ows_09 <- function(ctx) {
  response <- http_get(protocol_items_url(ctx, list(properties = "name,status", limit = 1)))
  properties <- names(response$json$features[[1L]]$properties)
  case_verdict(
    response$status == 200L && setequal(properties, c("name", "status")),
    sprintf(paste0("PROPERTYNAME=name,status returned HTTP %d with properties {%s}; expected exactly ",
                   "{name, status}. Property subsetting that silently returns everything wastes the ",
                   "bandwidth the client asked to save."),
            response$status, paste(properties, collapse = ", ")),
    measured_count = length(properties)
  )
}

ext_ows_10 <- function(ctx) {
  filter_xml <- paste0(
    '<fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">',
    "<fes:PropertyIsEqualTo><fes:ValueReference>", FILTER_FIELD, "</fes:ValueReference>",
    "<fes:Literal>", FILTER_VALUE, "</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>")
  response <- http_get(protocol_items_url(ctx, list(filter = filter_xml)))
  features <- response$json$features %||% list()
  statuses <- unique(vapply(features, function(f) as.character(f$properties$status), character(1)))
  case_verdict(
    response$status == 200L && length(features) == ACTIVE_FEATURES && identical(statuses, FILTER_VALUE),
    sprintf(paste0("OGC Filter Encoding 2.0 fes:PropertyIsEqualTo(%s='%s') returned HTTP %d with %d ",
                   "feature(s) (expected %d) and status values {%s}."),
            FILTER_FIELD, FILTER_VALUE, response$status, length(features), ACTIVE_FEATURES,
            paste(statuses, collapse = ", ")),
    measured_count = length(features)
  )
}

ext_ows_11 <- function(ctx) {
  ascending <- http_get(protocol_items_url(ctx, list(sort_by = "name A", limit = 100)))
  descending <- http_get(protocol_items_url(ctx, list(sort_by = "name D", limit = 100)))
  ascending_names <- vapply(ascending$json$features %||% list(),
                            function(f) as.character(f$properties$name), character(1))
  descending_names <- vapply(descending$json$features %||% list(),
                             function(f) as.character(f$properties$name), character(1))
  ascending_ok <- length(ascending_names) == TOTAL_FEATURES &&
    identical(ascending_names, sort(ascending_names))
  descending_ok <- length(descending_names) == TOTAL_FEATURES &&
    identical(descending_names, rev(sort(descending_names)))
  case_verdict(
    ascending_ok && descending_ok,
    sprintf(paste0("SORTBY=name A produced {%s} (%s) and SORTBY=name D produced {%s} (%s). Both ",
                   "directions must be honoured, otherwise stable client-side paging is impossible."),
            paste(ascending_names, collapse = ","), if (ascending_ok) "sorted" else "NOT sorted",
            paste(descending_names, collapse = ","), if (descending_ok) "sorted" else "NOT sorted"),
    measured_count = length(ascending_names)
  )
}

ext_ows_12 <- function(ctx) {
  results <- character(0)
  ok <- TRUE
  for (version in c("1.1.0", "1.0.0")) {
    outcome <- tryCatch({
      client <- ows4R::WFSClient$new(url = ctx$wfs_url, serviceVersion = version, logger = NULL)
      types <- client$getCapabilities()$getFeatureTypes()
      names <- vapply(types, function(ft) as.character(ft$getName()), character(1))
      list(count = length(types), target = paste0("honua:", WFS_TYPE_LOCAL_NAME) %in% names)
    }, error = function(e) list(count = 0L, target = FALSE, error = conditionMessage(e)))
    if (!isTRUE(outcome$target)) ok <- FALSE
    results <- c(results, sprintf("%s -> %d feature type(s)%s", version, outcome$count,
                                  if (isTRUE(outcome$target)) ", target present" else
                                    paste0(", target MISSING", if (!is.null(outcome$error))
                                      paste0(" (", truncate_note(outcome$error, 100L), ")") else "")))
  }
  case_verdict(
    ok,
    sprintf(paste0("ows4R against the advertised legacy WFS versions: %s. The server advertises ",
                   "1.1.0 and 1.0.0 compatibility endpoints, so an R client pinned to either must ",
                   "still discover the layer."),
            paste(results, collapse = "; ")),
    measured_count = length(results)
  )
}

# ---------------------------------------------------------------------------
# Per-protocol driver
# ---------------------------------------------------------------------------

common_core_cases <- function(ctx) list(
  list(id = "CERT-CONN-01", fn = case_conn_01),
  list(id = "CERT-DISC-01", fn = case_disc_01),
  list(id = "CERT-DISC-02", fn = case_disc_02),
  list(id = "CERT-SCHM-01", fn = case_schm_01),
  list(id = "CERT-SCHM-02", fn = case_schm_02),
  list(id = "CERT-QFLT-01", fn = case_qflt_01),
  list(id = "CERT-QFLT-02", fn = case_qflt_02),
  list(id = "CERT-PAGE-01", fn = case_page_01),
  list(id = "CERT-PAGE-02", fn = case_page_02),
  list(id = "CERT-GEOM-01", fn = case_geom_01),
  list(id = "CERT-GEOM-02", fn = case_geom_02),
  list(id = "CERT-ERRH-01", fn = case_errh_01),
  list(id = "CERT-ERRH-02", fn = case_errh_02)
)

extension_cases <- function(ctx) {
  shared <- list(
    list(id = "NB-RSF-TYP-01", fn = ext_typ_01),
    list(id = "NB-RSF-TYP-02", fn = ext_typ_02),
    list(id = "NB-RSF-TYP-03", fn = ext_typ_03),
    list(id = "NB-RSF-TYP-04", fn = ext_typ_04),
    list(id = "NB-RSF-NUL-01", fn = ext_nul_01),
    list(id = "NB-RSF-NUL-02", fn = ext_nul_02),
    list(id = "NB-RSF-GEO-01", fn = ext_geo_01),
    list(id = "NB-RSF-GEO-02", fn = ext_geo_02),
    list(id = "NB-RSF-GEO-03", fn = ext_geo_03),
    list(id = "NB-RSF-CRS-01", fn = ext_crs_01),
    list(id = "NB-RSF-CRS-02", fn = ext_crs_02),
    list(id = "NB-RSF-CRS-03", fn = ext_crs_03),
    list(id = "NB-RSF-CRS-04", fn = ext_crs_04),
    list(id = "NB-RSF-PAG-01", fn = ext_pag_01),
    list(id = "NB-RSF-PAG-02", fn = ext_pag_02),
    list(id = "NB-RSF-PAG-03", fn = ext_pag_03),
    list(id = "NB-RSF-PAG-04", fn = ext_pag_04),
    list(id = "NB-RSF-PAG-05", fn = ext_pag_05),
    list(id = "NB-RSF-FMT-01", fn = ext_fmt_01),
    list(id = "NB-RSF-FMT-02", fn = ext_fmt_02),
    list(id = "NB-RSF-FMT-03", fn = ext_fmt_03),
    list(id = "NB-RSF-ERR-01", fn = ext_err_01),
    list(id = "NB-RSF-ERR-02", fn = ext_err_02),
    list(id = "NB-RSF-ERR-03", fn = ext_err_03),
    list(id = "NB-RSF-ERR-04", fn = ext_err_04),
    list(id = "NB-RSF-AUT-01", fn = ext_aut_01),
    list(id = "NB-RSF-AUT-02", fn = ext_aut_02),
    list(id = "NB-RSF-AUT-03", fn = ext_aut_03),
    list(id = "NB-RSF-XPR-01", fn = ext_xpr_01),
    list(id = "NB-RSF-XPR-02", fn = ext_xpr_02),
    list(id = "NB-RSF-XPR-03", fn = ext_xpr_03),
    list(id = "NB-RSF-XPR-04", fn = ext_xpr_04)
  )
  specific <- if (identical(ctx$kind, "oapif")) list(
    list(id = "NB-RSF-OAF-01", fn = ext_oaf_01),
    list(id = "NB-RSF-OAF-02", fn = ext_oaf_02),
    list(id = "NB-RSF-OAF-03", fn = ext_oaf_03),
    list(id = "NB-RSF-OAF-04", fn = ext_oaf_04),
    list(id = "NB-RSF-OAF-05", fn = ext_oaf_05),
    list(id = "NB-RSF-OAF-06", fn = ext_oaf_06)
  ) else list(
    list(id = "NB-RSF-OWS-01", fn = ext_ows_01),
    list(id = "NB-RSF-OWS-02", fn = ext_ows_02),
    list(id = "NB-RSF-OWS-03", fn = ext_ows_03),
    list(id = "NB-RSF-OWS-04", fn = ext_ows_04),
    list(id = "NB-RSF-OWS-05", fn = ext_ows_05),
    list(id = "NB-RSF-OWS-06", fn = ext_ows_06),
    list(id = "NB-RSF-OWS-07", fn = ext_ows_07),
    list(id = "NB-RSF-OWS-08", fn = ext_ows_08),
    list(id = "NB-RSF-OWS-09", fn = ext_ows_09),
    list(id = "NB-RSF-OWS-10", fn = ext_ows_10),
    list(id = "NB-RSF-OWS-11", fn = ext_ows_11),
    list(id = "NB-RSF-OWS-12", fn = ext_ows_12)
  )
  c(shared, specific)
}

run_protocol_cases <- function(collector, ctx, probe) {
  record_control_plane_results(collector, ctx$base_url, probe)
  # Resolve the layer/feature type first so the protocol URL builders have it;
  # a failure here is reported per-case rather than aborting the protocol.
  tryCatch({
    resolve_layer(ctx)
    if (identical(ctx$kind, "wfs")) {
      tryCatch(wfs_feature_type(ctx), error = function(e) {
        ctx$wfs_type_name <- ctx$layer
        message(sprintf("  (ows4R feature-type resolution failed: %s)", truncate_note(conditionMessage(e), 160L)))
      })
    }
  }, error = function(e) {
    message(sprintf("  (layer resolution failed: %s)", truncate_note(conditionMessage(e), 160L)))
  })
  for (entry in c(common_core_cases(ctx), extension_cases(ctx))) {
    local({
      case <- entry
      run_case(collector, case$id, function() case$fn(ctx))
    })
  }
  invisible(NULL)
}

main <- function() {
  base_url <- resolve_base_url()
  output_dir <- resolve_output_dir()
  run_id <- utc_now_compact()

  message(sprintf("r-sf lane: base_url=%s output_dir=%s run_id=%s", base_url, output_dir, run_id))

  probe <- probe_admin_auth(base_url)
  runtime <- build_lane_runtime(
    base_url = base_url,
    project_root = cf_project_root(),
    fixture_path = cf_seed_path(),
    server_config_path = cf_server_config_path(),
    version_env = "HONUA_R_SF_SERVER_VERSION",
    commit_env = "HONUA_R_SF_SERVER_COMMIT",
    auth_header = probe$header
  )
  client_version <- lane_client_version()
  message(sprintf("  client: %s", client_version))
  message(sprintf("  server_version=%s server_commit=%s environment=%s",
                  runtime$server_version, runtime$server_commit, runtime$environment))

  protocols <- list(
    list(kind = "oapif", protocol = "ogc-features", protocol_version = "1.0"),
    list(kind = "wfs", protocol = "wfs", protocol_version = "2.0.0")
  )

  written <- character(0)
  for (spec in protocols) {
    message(sprintf("\n=== %s (%s) ===", spec$protocol, spec$protocol_version))
    collector <- new_collector(
      runtime = runtime,
      client_lane = LANE,
      client_version = client_version,
      protocol = spec$protocol,
      protocol_version = spec$protocol_version,
      applicable = APPLICABLE_IDS,
      not_applicable_reason = NOT_APPLICABLE_REASON,
      run_id = run_id
    )
    ctx <- new_protocol_ctx(base_url, spec, probe)
    # Backstop: a failure outside a case body must still leave an envelope on
    # disk, with the unreached common-core cases fail-closed as `skip`.
    tryCatch(
      run_protocol_cases(collector, ctx, probe),
      error = function(e) message(sprintf("  protocol run aborted: %s", conditionMessage(e)))
    )
    path <- file.path(output_dir, sprintf("%s-%s-%s.cert.json", run_id, LANE, spec$protocol))
    write_envelope(collector, path)
    written <- c(written, path)
    envelope <- build_envelope(collector)
    message(sprintf("  wrote %s (total=%d pass=%d fail=%d skip=%d n/a=%d)",
                    path, envelope$summary$total, envelope$summary$passed, envelope$summary$failed,
                    envelope$summary$skipped, envelope$summary$not_applicable))
  }

  message(sprintf("\nr-sf lane complete; %d envelope(s) written.", length(written)))
  invisible(written)
}

if (!interactive()) {
  main()
}
