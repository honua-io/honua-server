# Bounded multidimensional worker evidence, 2026-10-28

Refs #3407 and #6704. This is engineering evidence, not a frozen campaign result or production-image qualification.

Two independently generated inputs went through the actual administrative registration/refresh API, the managed GDAL worker's `/vsis3/` conversion, derived-store registration, and canonical Zarr/xarray HTTP readback through an S3 emulator. Both jobs succeeded. Each derived store contains 46 archived objects; all 512 raw values, two fill samples, coordinates, CF attributes and decoded time matched. Both isolated stacks were stopped after evidence collection.

## Identity

Managed server and worker source: `57d2c31132e52147d79ca702db34b70e7954abac`.
[CI run 40607031200](https://github.com/honua-io/honua-server/actions/runs/40607031200) produced:

| Component | CI artifact ID | Managed archive SHA256 | Diagnostic image ID |
|---|---|---|---|
| Server |12782164814|`d1d4de8dbf20db994c242ba51e1f0a800edefdcb8eb94fa5e1d16e1398449fe1`|`sha256:458cacbfb57e3bddde46472de71fa0d62f8163bf43608b6c75a580b7b9d2fecb`|
| Worker |12782412570|`548925b10c830c20a22e528f7185d1c658309fba22dc3718972bb056b04f1737`|`sha256:dc3ec376f6e9147db348688fcf6405ad51caa6ac413fb2973bc7cb7ff4402dda`|

The worker used provenance-verified native-tools image `ghcr.io/honua-io/honua-server-native-tools@sha256:6561e8f6018e967aa99a0125e82fa7d4411b79dc9381fbe82d0cd373958c8415`, source `66b28d9a17b6f83675d92f4cdf4d00dac2f5e2c9`, input identity `309de81da452fbbd4be4a703f85491d7aae270d46dce1e95e252285364d9bea8`. Server runtime base was `mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac`; runtime 10.0.13 was verified from image files. These are diagnostic assemblies of source-bound CI outputs, not published production/AOT images. `source-bindings.json` retains the three principal assembly-file hashes for each component and native provenance is retained separately.

## Actual jobs and readback

| Input | Job | Input SHA256 | Archived bytes |
|---|---|---|---|
| NetCDF4 |`covscan-b219aa38ca064577b19ed8679385e01f`|`545e859729fff81ee233b49e953af364ea9c7359501adc6b1e2a24f76b41f3a1`|5287|
| Standalone HDF5 |`covscan-c399f6ddf75a46af9b5507c75d047381`|`4d2bf70598174577311e5c9863d0475b8c8066d92cb5e2e66f80f2da5ec0daf7`|5283|

The inputs have shape 4x8x16, Float32 temperature, chunks 1x4x4, fill -9999 at indices 0 and 255, and independent NetCDF/HDF5 construction. They are not two names for one NetCDF file. Job receipts bind original/refreshed registration identity, job timestamps/commands, object listings and hashes. HTTP readbacks used Zarr 3.4.2, xarray 2026.9.0 and NumPy 2.5.4, with `consolidated=True` for the listingless HTTP store.

`native-libraries.json` records actual GDAL opens in the worker container, then resolved loaded library paths/hashes. Observed versions were GDAL 3.13.1, HDF5 1.14.6 and netCDF 4.9.3; HDF5 thread-safety and deflate/shuffle/fletcher32 filter support were checked. High-level HDF5 dependency API results are not an independent second core-library instance.

## Contents and verification

- `summary.json`: exact scope, jobs, images, resources and limitations.
- `nc-job-receipt.json`, `h5-job-receipt.json`: original bound API/worker/object receipts, unchanged.
- `nc-http-readback.json`, `h5-http-readback.json`: actual canonical HTTP outputs, unchanged.
- `source-bindings.json`, `native-tools-provenance.json`, `native-libraries.json`: compact identities and native evidence.
- `fixtures-and-worker-output.tar.gz`: two original inputs, both 46-object worker stores, and historical readback source; no 92 loose repository object files.
- `readback-source-provenance.json`: reconstructed source variants from the exact in-memory transforms used for successful reads. This is reconstructed provenance, not a new execution. Original interpreter was the isolated `netcdf-fidelity-venv/bin/python`; the two store URLs are recorded without credentials. Archived variants target the stopped isolated stacks and were not rerun while preparing this bundle.
- `privacy-review.json`: selection/exclusions. No credentials, environment files, authorization headers, full application logs or 1000-file extraction manifests are included.
- `SHA256SUMS`: integrity of every other file in this directory.

Use `sha256sum -c SHA256SUMS`, inspect the tar member list, and extract into a fresh directory if needed. Every output object's size/hash was checked against its original job receipt before archiving; the original job and HTTP receipt hashes match `summary.json`. The tar uses sorted paths and normalized timestamps/ownership.

## Limits

- Not published production/AOT image qualification; diagnostic containers ran with their recorded users/resources, not the final production permission setup.
- Not real AWS or frozen 24-row campaign qualification.
- No native HDF5 source range budget or representative cloud-layout performance proof. These 64-byte chunks demonstrate correctness only.
- No CoverageJSON pixel-serving proof: the seed publishes FeatureServer and STAC only; 404s from that absence are not a producer-defect claim.
- The time coordinate is int32, not the optional fixed fsspec int64 profile. No campaign cell is credited.
- Full-array canonical HTTP reads do not establish subset efficiency, public deployment authorization/CORS, or complete #3407 acceptance.
