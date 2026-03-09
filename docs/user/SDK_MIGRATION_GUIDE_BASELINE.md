# SDK Migration Guide Baseline

This document defines the structure and expectations for SDK migration guides across the JS, Python, and .NET SDKs. Each SDK repository should maintain a migration guide following this baseline.

## Migration Guide Structure

Every SDK release that changes the public API surface must include a migration section following this structure.

### Required Sections

1. **Version header**: `## Migrating from X.Y to X.Z`
2. **Breaking changes**: List of breaking changes with before/after code examples
3. **New features**: List of new capabilities with usage examples
4. **Deprecations**: List of deprecated APIs with replacement guidance and sunset timeline
5. **Server compatibility**: Minimum and maximum server versions for this SDK release

### Example Migration Entry

```markdown
## Migrating from 0.2.0 to 0.3.0

### Server Compatibility

- Minimum server version: **1.1.0**
- Maximum server version: **—** (no upper bound)

### Breaking Changes

#### `queryFeatures` return type changed

The `queryFeatures` method now returns a `FeatureSet` object instead of a raw
array of features. Access the features via the `.features` property.

**Before (0.2.x):**

    const features = await client.queryFeatures({ where: "status = 'active'" });
    features.forEach(f => console.log(f.attributes));

**After (0.3.0):**

    const result = await client.queryFeatures({ where: "status = 'active'" });
    result.features.forEach(f => console.log(f.attributes));
    // result.exceededTransferLimit is now available

### New Features

#### Streaming pagination

`queryFeaturesStream()` returns an async generator that handles server-side
pagination transparently.

    for await (const feature of client.queryFeaturesStream({ where: "1=1" })) {
      process.stdout.write(JSON.stringify(feature) + "\n");
    }

### Deprecations

#### `queryAll()` deprecated in favor of `queryFeaturesStream()`

`queryAll()` accumulates all pages in memory before returning. Use
`queryFeaturesStream()` for memory-efficient streaming.

- **Replacement**: `queryFeaturesStream()`
- **Sunset**: Will be removed in 0.5.0
```

## Changelog Format

SDK changelogs should follow [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format:

```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- New feature descriptions

### Changed
- Modifications to existing features

### Deprecated
- Features that will be removed in a future release

### Removed
- Features removed in this release

### Fixed
- Bug fixes

### Security
- Vulnerability fixes
```

## Release Note Template

Each SDK release should include a release note with the following structure:

```markdown
# @honua/sdk-js v0.3.0

## Highlights

- One-line summary of the most important change in this release.

## Server Compatibility

| Minimum Server | Maximum Server | Recommended Server |
|----------------|----------------|--------------------|
| 1.1.0 | — | 1.2.0+ |

## Breaking Changes

- List of breaking changes (link to migration section for details)

## What's New

- Bulleted list of new features

## Deprecations

- List of deprecated APIs with sunset versions

## Bug Fixes

- List of bug fixes

## Full Changelog

See [CHANGELOG.md](CHANGELOG.md) for the complete list of changes.
```

## Version Status and Release Channels

SDK version status maps to release channels as follows:

| SDK Pre-Release Tag | SDK Status | Compatibility Guarantee |
|---------------------|-----------|------------------------|
| `-alpha.N` / `aN` | Alpha | None. API may change without notice. |
| `-beta.N` / `bN` | Beta | API is stabilizing. Breaking changes require migration notes in the same release. |
| `-rc.N` / `rcN` | Beta | Release candidate. No new breaking changes expected. |
| (no tag) | Stable | Semver. Breaking changes only in major versions. |
| (LTS designation) | LTS | Security fixes only. No API changes. Supported for 12+ months. |

During alpha, migration guides are optional but recommended. Starting from beta, migration guides are required for any release that changes the public API surface.

## Per-SDK Expectations

### `@honua/sdk-js`

- Migration guide location: `MIGRATION.md` in the repo root
- Language: TypeScript code examples
- Package manager: npm
- Changelog: `CHANGELOG.md`

### `honua-sdk` (Python)

- Migration guide location: `MIGRATION.md` in the repo root
- Language: Python code examples
- Package manager: pip / PyPI
- Changelog: `CHANGELOG.md`

### `Honua.Sdk.Grpc` and `Honua.Sdk.Admin` (.NET)

- Migration guide location: `MIGRATION.md` in the repo root (shared across both packages)
- Language: C# code examples
- Package manager: NuGet
- Changelog: `CHANGELOG.md`

## Related Documentation

- [SDK Compatibility Matrix](SDK_VERSION_MATRIX.md) — version compatibility and protocol coverage
- [Control Plane Migration Guide](CONTROL_PLANE_MIGRATION_GUIDE.md) — admin API migration (server-side)
- [Control Plane Versioning Policy](CONTROL_PLANE_VERSIONING_POLICY.md) — server versioning and deprecation policy
