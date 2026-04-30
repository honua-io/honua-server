# WCS 2.0 CITE Conformance Testing

Run the WCS 2.0.1 CITE harness locally from the repository root:

```bash
./scripts/conformance/cite/run-cite-wcs20-tests.sh
```

The script builds `honua-server:latest` unless `HONUA_CITE_SKIP_BUILD=true` is
set, starts Honua Server, PostGIS, Redis, and the official
`ogccite/ets-wcs20:1.22-teamengine-6.0.0-RC2` TeamEngine image, then writes
artifacts to `cite-wcs20-results/`.

## Profiles

- `core` runs the WCS core profile and is the default.
- `crs` runs core plus the CRS extension.
- `extensions` adds POST, processing, scaling, interpolation, range subsetting,
  and CRS extension checks.
- `full` adds EO-WCS checks.

Example:

```bash
./scripts/conformance/cite/run-cite-wcs20-tests.sh --profile core --verbose
```

## Results

The stable results directory is `cite-wcs20-results/`. It includes captured WCS
responses, TeamEngine XML/HTML reports when available, container logs, a
normalized `cite-compliance-report.xml`, `expected-known-failures.md`, and
`cite-wcs20-summary.md`.

## Expected Thin-Slice Limitations

The current WCS implementation intentionally covers a thin WCS 2.0.1 KVP slice.
Failures are expected for XML POST/SOAP bindings, GML coverage output, WCPS and
processing, scaling, interpolation, range subsetting, broad CRS extension
coverage, and EO-WCS. Treat missing result files, zero executable tests, failed
GetCapabilities, or missing seeded coverages as harness failures rather than
known protocol limitations.

The scheduled workflow is `.github/workflows/cite-wcs20-conformance.yml`. It is
manual/scheduled only and is not part of normal PR or push gates.
