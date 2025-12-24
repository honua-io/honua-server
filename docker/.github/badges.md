# Workflow Status Badges

Add these badges to your main README.md to show workflow status:

## CI Status
```markdown
[![CI](https://github.com/YOUR_ORG/honua-server/actions/workflows/ci.yml/badge.svg)](https://github.com/YOUR_ORG/honua-server/actions/workflows/ci.yml)
```

## Nightly Tests
```markdown
[![Nightly Tests](https://github.com/YOUR_ORG/honua-server/actions/workflows/nightly.yml/badge.svg)](https://github.com/YOUR_ORG/honua-server/actions/workflows/nightly.yml)
```

## Release
```markdown
[![Release](https://github.com/YOUR_ORG/honua-server/actions/workflows/release.yml/badge.svg)](https://github.com/YOUR_ORG/honua-server/actions/workflows/release.yml)
```

## CodeQL Security
```markdown
[![CodeQL](https://github.com/YOUR_ORG/honua-server/actions/workflows/ci.yml/badge.svg?event=schedule)](https://github.com/YOUR_ORG/honua-server/security/code-scanning)
```

## All Badges Combined
```markdown
[![CI](https://github.com/YOUR_ORG/honua-server/actions/workflows/ci.yml/badge.svg)](https://github.com/YOUR_ORG/honua-server/actions/workflows/ci.yml)
[![Nightly Tests](https://github.com/YOUR_ORG/honua-server/actions/workflows/nightly.yml/badge.svg)](https://github.com/YOUR_ORG/honua-server/actions/workflows/nightly.yml)
[![Release](https://github.com/YOUR_ORG/honua-server/actions/workflows/release.yml/badge.svg)](https://github.com/YOUR_ORG/honua-server/actions/workflows/release.yml)
[![Security Rating](https://sonarcloud.io/api/project_badges/measure?project=YOUR_PROJECT&metric=security_rating)](https://sonarcloud.io/dashboard?id=YOUR_PROJECT)
```

## Custom Badges

You can also create custom badges for specific metrics:

### CITE Conformance
```markdown
[![OGC CITE](https://img.shields.io/badge/OGC%20CITE-Conformant-green)](https://github.com/YOUR_ORG/honua-server/actions/workflows/nightly.yml)
```

### Coverage
```markdown
[![codecov](https://codecov.io/gh/YOUR_ORG/honua-server/branch/trunk/graph/badge.svg)](https://codecov.io/gh/YOUR_ORG/honua-server)
```

### Performance
```markdown
[![Performance](https://img.shields.io/badge/Performance-Tracked-blue)](https://github.com/YOUR_ORG/honua-server/actions/workflows/nightly.yml)
```