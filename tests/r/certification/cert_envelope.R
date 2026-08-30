# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

# Certification-envelope writer for the R `sf`/`ows4R` canonical-client lane.
#
# This is the R mirror of `tests/python/shared/cert_envelope.py`. The R lane
# cannot import the Python module, so this file reproduces the same envelope
# shape, field order, status vocabulary, worst-status-wins precedence, and the
# fail-closed "applicable but not executed -> skip" rule. The emitted JSON must
# parse into exactly the same structure the Python collector produces; the lane
# driver round-trips its output to prove it.
#
# The envelope schema is defined by
# `docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md`. Two fields are load-bearing
# for the certification gate:
#
#   fixture_revision / server_config_revision
#       Content-addressed digests of the fixture and server-configuration
#       inputs, required by `fixturePolicy.requiredReceiptFields` in
#       `docs/gis/data/client-certification-matrix.v1.json`. A lane that cannot
#       bind both is not allowed to claim canonical-fixture provenance.

suppressPackageStartupMessages({
  library(jsonlite)
  library(digest)
  library(httr)
})

# The 24-ID common core (18 base + 6 visual/style slice IDs), in the exact order
# declared by COMMON_CORE_IDS in tests/python/shared/cert_envelope.py. The
# `results` array must carry all 24 IDs in this order.
COMMON_CORE_IDS <- c(
  "CERT-CONN-01", "CERT-CONN-02",
  "CERT-AUTH-01", "CERT-AUTH-02",
  "CERT-DISC-01", "CERT-DISC-02",
  "CERT-SCHM-01", "CERT-SCHM-02",
  "CERT-QFLT-01", "CERT-QFLT-02",
  "CERT-PAGE-01", "CERT-PAGE-02",
  "CERT-GEOM-01", "CERT-GEOM-02",
  "CERT-ERRH-01", "CERT-ERRH-02",
  "CERT-RNDR-01", "CERT-RNDR-02",
  "CERT-RNDR-SYM-01", "CERT-RNDR-LIN-01", "CERT-RNDR-FIL-01",
  "CERT-RNDR-LBL-01", "CERT-RNDR-SPR-01", "CERT-RNDR-URL-01"
)

# Rendering facets. Data clients (GeoPandas, DuckDB, R sf, pystac-client) have
# no drawing surface at all, so these are structurally not-applicable rather
# than "not exercised yet".
RENDERING_IDS <- c(
  "CERT-RNDR-01", "CERT-RNDR-02",
  "CERT-RNDR-SYM-01", "CERT-RNDR-LIN-01", "CERT-RNDR-FIL-01",
  "CERT-RNDR-LBL-01", "CERT-RNDR-SPR-01", "CERT-RNDR-URL-01"
)

STATUS_RANK <- c("fail" = 3L, "pass" = 2L, "skip" = 1L, "not-applicable" = 1L)

# Geometry tolerance thresholds from CROSS_CLIENT_CERTIFICATION_MATRIX.md.
GEOGRAPHIC_TOLERANCE_DEGREES <- 1e-6
PROJECTED_TOLERANCE_METERS <- 0.01

SKIP_NOTE_NOT_EXECUTED <- "Applicable to this lane but not executed in this run."

# ---------------------------------------------------------------------------
# Path helpers
# ---------------------------------------------------------------------------

#' Directory holding this certification suite.
#'
#' `HONUA_R_CERT_DIR` wins when set (the container exports it); otherwise the
#' directory is derived from the `--file=` argument Rscript passes.
cert_script_dir <- function() {
  configured <- Sys.getenv("HONUA_R_CERT_DIR", "")
  if (nzchar(configured)) {
    return(normalizePath(configured, mustWork = TRUE))
  }
  args <- commandArgs(trailingOnly = FALSE)
  hit <- grep("^--file=", args, value = TRUE)
  if (length(hit) > 0L) {
    return(normalizePath(dirname(sub("^--file=", "", hit[[1L]])), mustWork = TRUE))
  }
  normalizePath(getwd(), mustWork = TRUE)
}

# ---------------------------------------------------------------------------
# Results
# ---------------------------------------------------------------------------

#' Normalise an optional numeric field to a JSON-safe scalar or NULL.
#'
#' R's NA must never reach the envelope: the schema wants JSON `null` for unset
#' numeric fields, and jsonlite would otherwise emit `"NA"` or `null` depending
#' on options. Length-0 vectors and NaN/Inf collapse to NULL as well.
cert_num <- function(value, integer = FALSE) {
  if (is.null(value)) return(NULL)
  if (length(value) == 0L) return(NULL)
  value <- value[[1L]]
  if (is.na(value)) return(NULL)
  if (!is.finite(value)) return(NULL)
  if (integer) as.integer(value) else as.numeric(value)
}

cert_chr <- function(value) {
  if (is.null(value) || length(value) == 0L) return("")
  value <- value[[1L]]
  if (is.na(value)) return("")
  as.character(value)
}

#' One CERT-* observation inside an evidence envelope.
#'
#' Field order matches `_as_dict` in cert_envelope.py and is load-bearing for
#' the emitted JSON.
cert_result <- function(test_case_id,
                        status,
                        duration_ms = NULL,
                        measured_count = NULL,
                        measured_delta = NULL,
                        notes = "",
                        evidence_ref = "",
                        client_identity = "") {
  if (!status %in% names(STATUS_RANK)) {
    stop(sprintf("unknown status '%s' for %s", status, test_case_id))
  }
  result <- list(
    test_case_id = as.character(test_case_id),
    status = as.character(status),
    duration_ms = cert_num(duration_ms, integer = TRUE),
    measured_count = cert_num(measured_count, integer = TRUE),
    measured_delta = cert_num(measured_delta),
    notes = cert_chr(notes),
    evidence_ref = cert_chr(evidence_ref)
  )
  if (nzchar(cert_chr(client_identity))) {
    result$client_identity <- cert_chr(client_identity)
  }
  result
}

cert_richness <- function(result) {
  score <- 0L
  if (!is.null(result$measured_count)) score <- score + 4L
  if (!is.null(result$measured_delta)) score <- score + 2L
  if (nzchar(result$evidence_ref)) score <- score + 2L
  if (nzchar(result$notes)) score <- score + 1L
  if (!is.null(result$duration_ms)) score <- score + 1L
  score
}

#' Worst-status-wins, ties broken toward the richer record.
cert_prefer <- function(candidate, existing) {
  candidate_rank <- STATUS_RANK[[candidate$status]]
  existing_rank <- STATUS_RANK[[existing$status]]
  if (candidate_rank != existing_rank) {
    return(candidate_rank > existing_rank)
  }
  cert_richness(candidate) > cert_richness(existing)
}

# ---------------------------------------------------------------------------
# Lane runtime (receipt bindings)
# ---------------------------------------------------------------------------

utc_now_iso <- function(now = Sys.time()) {
  paste0(format(as.POSIXct(now, tz = "UTC"), "%Y-%m-%dT%H:%M:%OS6", tz = "UTC"), "+00:00")
}

#' Compact UTC stamp used as `run_id` and as the envelope filename prefix.
#'
#' Must contain no "-": scripts/client-compat/refresh-baselines.sh strips the
#' filename up to the first "-" to derive a stable baseline name.
utc_now_compact <- function(now = Sys.time()) {
  format(as.POSIXct(now, tz = "UTC"), "%Y%m%dT%H%M%SZ", tz = "UTC")
}

file_digest <- function(path) {
  paste0("sha256:", digest::digest(file = path, algo = "sha256"))
}

#' Read the server version from the admin info endpoint.
#'
#' `/api/v1/admin/version` is admin-gated, so the anonymous probe is tried first
#' (mirroring read_server_version in cert_envelope.py) and, when it is rejected,
#' the discovered admin credential is retried so the receipt carries a real
#' version instead of "unknown". Any failure degrades to "unknown".
read_server_version <- function(base_url, override_env = "", auth_header = NULL) {
  if (nzchar(override_env)) {
    configured <- Sys.getenv(override_env, "")
    if (nzchar(configured)) return(configured)
  }
  url <- paste0(sub("/+$", "", base_url), "/api/v1/admin/version")
  attempt <- function(header) {
    tryCatch({
      response <- if (is.null(header)) {
        httr::GET(url, httr::timeout(15))
      } else {
        httr::GET(url, header, httr::timeout(15))
      }
      if (httr::status_code(response) >= 400) return(NULL)
      payload <- jsonlite::fromJSON(
        httr::content(response, as = "text", encoding = "UTF-8"),
        simplifyVector = FALSE
      )
      version <- payload$data$version
      if (is.null(version)) version <- payload$version
      if (is.null(version) || !nzchar(as.character(version))) return(NULL)
      as.character(version)
    }, error = function(e) NULL)
  }
  version <- attempt(NULL)
  if (is.null(version) && !is.null(auth_header)) {
    version <- attempt(auth_header)
  }
  if (is.null(version)) "unknown" else version
}

read_server_commit <- function(project_root, override_env = "") {
  if (nzchar(override_env)) {
    configured <- Sys.getenv(override_env, "")
    if (nzchar(configured)) return(configured)
  }
  tryCatch({
    commit <- suppressWarnings(system2(
      "git",
      c("-C", shQuote(project_root), "rev-parse", "HEAD"),
      stdout = TRUE,
      stderr = FALSE
    ))
    commit <- trimws(paste(commit, collapse = ""))
    if (!nzchar(commit)) "unknown" else commit
  }, error = function(e) "unknown")
}

#' Assemble the receipt bindings every envelope this lane emits must carry.
build_lane_runtime <- function(base_url,
                               project_root,
                               fixture_path,
                               server_config_path,
                               version_env = "",
                               commit_env = "",
                               auth_header = NULL) {
  normalized <- sub("/+$", "", base_url)
  list(
    base_url = normalized,
    environment = if (nzchar(Sys.getenv("CI", ""))) "ci" else "local",
    server_version = read_server_version(normalized, version_env, auth_header),
    server_commit = read_server_commit(project_root, commit_env),
    fixture_revision = file_digest(fixture_path),
    server_config_revision = file_digest(server_config_path)
  )
}

# ---------------------------------------------------------------------------
# Collector
# ---------------------------------------------------------------------------

#' Create a collector that accumulates CERT-* results for one protocol.
#'
#' `applicable` is the set of common-core IDs this lane is contractually
#' required to substantiate. Every other common-core ID is emitted as
#' `not-applicable` with `not_applicable_reason` in notes, so the envelope
#' always carries the full 24-ID vocabulary and a reader can tell "structurally
#' impossible" apart from "did not run".
new_collector <- function(runtime,
                          client_lane,
                          client_version,
                          protocol,
                          protocol_version,
                          applicable,
                          not_applicable_reason,
                          run_id = utc_now_compact()) {
  unknown <- setdiff(applicable, COMMON_CORE_IDS)
  if (length(unknown) > 0L) {
    stop(sprintf(
      "%s/%s declares unknown common-core IDs: %s",
      client_lane, protocol, paste(sort(unknown), collapse = ", ")
    ))
  }
  collector <- new.env(parent = emptyenv())
  collector$runtime <- runtime
  collector$client_lane <- client_lane
  collector$client_version <- client_version
  collector$protocol <- protocol
  collector$protocol_version <- protocol_version
  collector$applicable <- applicable
  collector$not_applicable_reason <- not_applicable_reason
  collector$run_id <- run_id
  collector$results <- list()
  collector$extensions <- list()
  collector
}

#' Record one observation, worst-status-wins.
collector_record <- function(collector,
                             test_case_id,
                             status,
                             duration_ms = NULL,
                             measured_count = NULL,
                             measured_delta = NULL,
                             notes = "",
                             evidence_ref = "",
                             client_identity = "") {
  if (test_case_id %in% COMMON_CORE_IDS && !(test_case_id %in% collector$applicable)) {
    stop(sprintf(
      paste0("%s/%s recorded %s, which it declares not-applicable; ",
             "fix the applicability set or stop recording the case."),
      collector$client_lane, collector$protocol, test_case_id
    ))
  }
  candidate <- cert_result(
    test_case_id = test_case_id,
    status = status,
    duration_ms = duration_ms,
    measured_count = measured_count,
    measured_delta = measured_delta,
    notes = notes,
    evidence_ref = evidence_ref,
    client_identity = client_identity
  )
  bucket <- if (test_case_id %in% COMMON_CORE_IDS) "results" else "extensions"
  existing <- collector[[bucket]][[test_case_id]]
  if (is.null(existing) || cert_prefer(candidate, existing)) {
    collector[[bucket]][[test_case_id]] <- candidate
  }
  invisible(candidate)
}

collector_has_records <- function(collector) {
  length(collector$results) > 0L || length(collector$extensions) > 0L
}

#' Materialise the envelope as a plain list, ready for jsonlite.
build_envelope <- function(collector) {
  results <- vector("list", length(COMMON_CORE_IDS))
  for (i in seq_along(COMMON_CORE_IDS)) {
    case_id <- COMMON_CORE_IDS[[i]]
    if (case_id %in% collector$applicable) {
      recorded <- collector$results[[case_id]]
      if (is.null(recorded)) {
        # Fail closed: an applicable case the run never reached is a gap, not a
        # pass.
        results[[i]] <- cert_result(case_id, "skip", notes = SKIP_NOTE_NOT_EXECUTED)
      } else {
        results[[i]] <- recorded
      }
    } else {
      results[[i]] <- cert_result(
        case_id, "not-applicable", notes = collector$not_applicable_reason
      )
    }
  }

  extensions <- unname(collector$extensions)
  # docs/gis/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md: `summary` aggregates the
  # `results` array only. Extension results are tracked separately in
  # `extensions` so a lane cannot inflate its common-core pass count with
  # lane-specific cases.
  statuses <- vapply(results, function(entry) entry$status, character(1))

  list(
    schema_version = "1.0",
    run_id = collector$run_id,
    run_date = utc_now_iso(),
    server_version = collector$runtime$server_version,
    server_commit = collector$runtime$server_commit,
    fixture_revision = collector$runtime$fixture_revision,
    server_config_revision = collector$runtime$server_config_revision,
    client_lane = collector$client_lane,
    client_version = collector$client_version,
    protocol = collector$protocol,
    protocol_version = collector$protocol_version,
    environment = collector$runtime$environment,
    results = unname(results),
    summary = list(
      total = length(statuses),
      passed = sum(statuses == "pass"),
      failed = sum(statuses == "fail"),
      skipped = sum(statuses == "skip"),
      not_applicable = sum(statuses == "not-applicable")
    ),
    cite_results = NULL,
    extensions = if (length(extensions) == 0L) list() else extensions
  )
}

#' Serialise an envelope to JSON.
#'
#' `auto_unbox` keeps scalars scalar, `null = "null"` emits JSON `null` (instead
#' of jsonlite's default `{}`) for the unset numeric fields and `cite_results`,
#' and `digits = NA` preserves full double precision for `measured_delta`.
envelope_json <- function(envelope) {
  jsonlite::toJSON(
    envelope,
    auto_unbox = TRUE,
    null = "null",
    na = "null",
    digits = NA,
    pretty = 2
  )
}

write_envelope <- function(collector, path) {
  dir.create(dirname(path), recursive = TRUE, showWarnings = FALSE)
  json <- envelope_json(build_envelope(collector))
  # Mirrors json.dumps(..., indent=2) + "\n" on the Python side.
  writeLines(as.character(json), con = path, useBytes = TRUE)
  invisible(path)
}
