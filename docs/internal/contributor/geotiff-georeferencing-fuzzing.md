# GeoTIFF Georeferencing Property Tests

`GeoTiffGeoreferencing` is a dependency-free metadata reader used to decide
whether delegated imagery output preserves its source ground. Its tests stay at
the same narrow boundary: they parse TIFF metadata and declared storage ranges,
but do not decode pixels or introduce GDAL/native dependencies.

## Fast PR tier

Run the bounded properties and regression corpus with:

```bash
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter "FullyQualifiedName~GeoTiffGeoreferencingPropertyTests"
```

The `Tier=Fast` lane runs 256 cases for each property. Arbitrary byte arrays are
capped at 4 KiB. Structured cases start with valid classic TIFF or BigTIFF
metadata in either byte order, then apply one bounded metadata mutation selected
from tag type, element count, offset, byte order, header version, IFD linkage,
storage range, transform value, or GeoKey-directory changes. This ensures the
properties exercise real parser structure instead of relying only on random
bytes. The BigTIFF IFD entry loop already has a production cap of 4,096 entries;
the classic IFD loop is bounded by its 16-bit entry count and payload length.

FsCheck prints a replay value and the minimized input when a property fails. Use
that replay value on the `Property` attribute while diagnosing the failure, then
promote a useful minimized case into `GeoTiffFuzzCorpus.RegressionCases()` before
removing the temporary replay setting.

## Longer local soak

The soak uses the same deterministic structured mutation grammar as the PR
properties. It is disabled by default so an accidental full Server test run does
not become unbounded. The default enabled run is 100,000 cases; the configured
count is clamped to 1 through 1,000,000 cases.

PowerShell:

```powershell
$env:HONUA_GEOTIFF_FUZZ_SOAK = '1'
$env:HONUA_GEOTIFF_FUZZ_ITERATIONS = '250000'
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj `
  --filter "FullyQualifiedName~TryRead_LocalStructuredMutationSoak_WhenEnabled"
Remove-Item Env:HONUA_GEOTIFF_FUZZ_SOAK
Remove-Item Env:HONUA_GEOTIFF_FUZZ_ITERATIONS
```

Bash:

```bash
HONUA_GEOTIFF_FUZZ_SOAK=1 \
HONUA_GEOTIFF_FUZZ_ITERATIONS=250000 \
dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
  --filter "FullyQualifiedName~TryRead_LocalStructuredMutationSoak_WhenEnabled"
```

The input sequence is deterministic by iteration number. A failure message names
the iteration, container, byte order, mutation, and seed, so the case can be
reproduced by constructing the reported `StructuredTiffInput` and then added to
the regression corpus.

## Regression corpus and findings

The checked-in corpus covers:

- headers without declared raster storage, out-of-bounds storage, and truncated
  multi-strip and tiled storage;
- BigTIFF offsets near `ulong.MaxValue` and element counts larger than the
  available payload;
- wrong model-tag field types, non-finite transforms, negative zero and subnormal
  scales, and tiepoint counts that are not a multiple of six;
- rotated, sheared, and axis-flipped model transformations;
- PixelIsPoint versus PixelIsArea normalization and user-defined CRS value 32767;
- mixed-endian headers, unsupported versions, self-referential/nested next-IFD
  links, conflicting duplicate tags, GeoKey count mismatches, and duplicate or
  out-of-order GeoKeys.

The initial property audit found two correctness gaps and tightened one related
metadata rule:

1. `DescribeMismatchAgainst` could report a match when NaN or infinity caused
   every tolerance comparison to evaluate false. It now refuses unusable source
   or output metadata before doing arithmetic.
2. A positive width and scale could underflow to a zero extent. A usable
   georeferencing block now requires finite, positive extents.
3. ModelPixelScale and ModelTransformation now require their specification-sized
   element counts, while ModelTiepoint requires a positive multiple of six.

Two parser behaviors are intentionally retained. The reader examines only the
first IFD because its contract needs one image's georeferencing, so next-IFD
cycles cannot create traversal or allocation. It also scans GeoKeys by identifier
rather than requiring sort order and uses the first supported inline CRS key;
this accepts producer ordering differences while all reads remain bounded by the
declared directory payload. Materially different parsed ground is still rejected
by the independent comparison property.
