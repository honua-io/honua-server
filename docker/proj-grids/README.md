# PROJ grid-data PostGIS image

The default customer Compose deployment builds this image with
`pgrouting/pgrouting:17-3.5-3.7.3` as its base. It retains PostgreSQL 17 and the
existing data-volume layout while adding the regional grids used by the datum
catalog. The install archive includes this build context under `docker/proj-grids`.
Run `python3 quickstart.py` from the extracted archive as described in the
[quickstart](../../docs/get-started/quickstart.md).

[`grids.txt`](grids.txt) lists the files downloaded from <https://cdn.proj.org>
at image build time. [`fetch-grids.sh`](fetch-grids.sh) fails on HTTP errors or
empty downloads. The image sets `PROJ_NETWORK=OFF`; transformations use the baked
files without runtime CDN access. Keep the image or its build cache for offline
restarts. An initial build needs access to the image registry, Debian package
repositories and PROJ CDN. Downloads run in a separate Debian build stage; the
database image receives only the grid files.

The manifest includes CONUS, Alaska, Canadian NTv2, Old Hawaiian, and Puerto
Rico/US Virgin Islands horizontal grids, plus the existing NADCON5 and geoid
files. Installing a file does not enable a vertical transformation or add a
catalog operation.

## Standalone build

The standalone diagnostic composition uses a separately pinned PostGIS base and
port 5436. It does not upgrade the customer deployment or its database volume:

```bash
docker compose -f docker/proj-grids/compose.yml up -d --build
```

To build the same base as the customer deployment:

```bash
docker build --build-arg BASE_IMAGE=pgrouting/pgrouting:17-3.5-3.7.3 \
  -t honua-postgis-proj-grids:17-3.5-3.7.3 docker/proj-grids
```

## Integration coverage

`DatumGridProvisioningTests` builds this Dockerfile with the customer base and
starts its own isolated PostGIS container. These integration tests run without
an environment opt-in or an external database URL. They exercise the catalog's
regional selections through `DatumTransformSql` on the provisioned image.
Separate grid-free fixtures retain coverage of the missing-grid error contract.

[Datum transformation parity](../../docs/reference/compatibility/datum-transformation-parity.md)
documents operation selection and reference coordinates.
