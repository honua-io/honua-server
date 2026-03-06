# Build Status Badges

This document contains the build status badges and markdown snippets for all Honua repositories.

## Repository Badge Templates

### honua-server

Copy and paste into the repository README:

```markdown
# Honua Server

[![CI](https://github.com/mikemcdougall/honua-server/workflows/CI/badge.svg)](https://github.com/mikemcdougall/honua-server/actions)
[![NuGet Publish](https://github.com/mikemcdougall/honua-server/workflows/Publish%20NuGet%20Packages/badge.svg)](https://github.com/mikemcdougall/honua-server/actions)
[![Terraform](https://github.com/mikemcdougall/honua-server/workflows/Terraform%20CI%2FCD/badge.svg)](https://github.com/mikemcdougall/honua-server/actions)
[![codecov](https://codecov.io/gh/mikemcdougall/honua-server/branch/trunk/graph/badge.svg)](https://codecov.io/gh/mikemcdougall/honua-server)
[![License: ELv2](https://img.shields.io/badge/License-ELv2-blue.svg)](https://github.com/mikemcdougall/honua-server/blob/trunk/LICENSE)

> Open-source geospatial feature server with multi-protocol support (GeoServices, OGC, gRPC)
```

### honua-core-sdk

Copy and paste into the repository README:

```markdown
# Honua Core SDK

[![CI/CD](https://github.com/mikemcdougall/honua-core-sdk/workflows/Honua%20Core%20SDK%20CI%2FCD/badge.svg)](https://github.com/mikemcdougall/honua-core-sdk/actions)
[![NuGet Version](https://img.shields.io/nuget/v/Honua.Core?logo=nuget)](https://www.nuget.org/packages/Honua.Core/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Honua.Core?logo=nuget)](https://www.nuget.org/packages/Honua.Core/)
[![codecov](https://codecov.io/gh/mikemcdougall/honua-core-sdk/branch/main/graph/badge.svg)](https://codecov.io/gh/mikemcdougall/honua-core-sdk)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://github.com/mikemcdougall/honua-core-sdk/blob/main/LICENSE)

> Core domain models and abstractions for Honua geospatial platform

## Installation

```bash
dotnet add package Honua.Core
```

## Supported Frameworks

- .NET 10.0
- .NET Standard 2.1
- .NET 8.0
- Xamarin.iOS
- Xamarin.Android
- .NET MAUI
```

### honua-admin-tools

Copy and paste into the repository README:

```markdown
# Honua Admin Tools

[![Multi-Language CI/CD](https://github.com/mikemcdougall/honua-admin-tools/workflows/Honua%20Admin%20Tools%20Multi-Language%20CI%2FCD/badge.svg)](https://github.com/mikemcdougall/honua-admin-tools/actions)
[![NuGet Version](https://img.shields.io/nuget/v/Honua.Admin.Sdk?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Honua.Admin.Sdk/)
[![npm version](https://img.shields.io/npm/v/@honua/admin-tools?logo=npm&label=NPM)](https://www.npmjs.com/package/@honua/admin-tools)
[![PyPI version](https://img.shields.io/pypi/v/honua-admin?logo=python&label=PyPI)](https://pypi.org/project/honua-admin/)
[![codecov](https://codecov.io/gh/mikemcdougall/honua-admin-tools/branch/main/graph/badge.svg)](https://codecov.io/gh/mikemcdougall/honua-admin-tools)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://github.com/mikemcdougall/honua-admin-tools/blob/main/LICENSE)

> Multi-language administrative tools for Honua geospatial infrastructure

## Installation

### .NET
```bash
dotnet add package Honua.Admin.Sdk
```

### Node.js
```bash
npm install @honua/admin-tools
```

### Python
```bash
pip install honua-admin
```

## Language Support

- 🟦 **.NET**: Full SDK with typed models
- 🟨 **Node.js**: TypeScript-enabled admin tools
- 🐍 **Python**: CLI tools and SDK
```

### geospatial-grpc

Copy and paste into the repository README:

```markdown
# Geospatial gRPC Protocols

[![Protocol CI/CD](https://github.com/mikemcdougall/geospatial-grpc/workflows/Geospatial%20gRPC%20Protocol%20CI%2FCD/badge.svg)](https://github.com/mikemcdougall/geospatial-grpc/actions)
[![Buf Validate](https://github.com/mikemcdougall/geospatial-grpc/workflows/Buf%20Validation%20%26%20Linting/badge.svg)](https://github.com/mikemcdougall/geospatial-grpc/actions)
[![Buf Registry](https://img.shields.io/badge/Buf-Registry-blue?logo=buffer)](https://buf.build/mikemcdougall/geospatial-grpc)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://github.com/mikemcdougall/geospatial-grpc/blob/main/LICENSE)

> Open gRPC protocol definitions for geospatial services

## Quick Start

### Using Buf CLI
```bash
# Add to your buf.yaml dependencies
buf dep update
buf generate buf.build/mikemcdougall/geospatial-grpc
```

### Download Clients
```bash
# Download pre-generated clients
curl -LO https://github.com/mikemcdougall/geospatial-grpc/releases/latest/download/geospatial-grpc-clients-latest.tar.gz
```

## Supported Languages

- **C#/.NET**: Full gRPC client support
- **JavaScript/Node.js**: gRPC-Web compatible
- **Python**: grpcio-tools integration
- **Go**: Protocol buffer and gRPC support

## Protocol Documentation

📖 [API Reference](https://mikemcdougall.github.io/geospatial-grpc/api-reference)
```

## Quality Indicators

### Additional Badge Options

You can enhance the README files with these additional badges:

```markdown
<!-- Release Information -->
[![GitHub Release](https://img.shields.io/github/v/release/mikemcdougall/REPO_NAME?logo=github)](https://github.com/mikemcdougall/REPO_NAME/releases)
[![GitHub Release Date](https://img.shields.io/github/release-date/mikemcdougall/REPO_NAME?logo=github)](https://github.com/mikemcdougall/REPO_NAME/releases)

<!-- Activity Indicators -->
[![GitHub last commit](https://img.shields.io/github/last-commit/mikemcdougall/REPO_NAME?logo=github)](https://github.com/mikemcdougall/REPO_NAME/commits)
[![GitHub commit activity](https://img.shields.io/github/commit-activity/m/mikemcdougall/REPO_NAME?logo=github)](https://github.com/mikemcdougall/REPO_NAME/graphs/commit-activity)

<!-- Issue Tracking -->
[![GitHub issues](https://img.shields.io/github/issues/mikemcdougall/REPO_NAME?logo=github)](https://github.com/mikemcdougall/REPO_NAME/issues)
[![GitHub pull requests](https://img.shields.io/github/issues-pr/mikemcdougall/REPO_NAME?logo=github)](https://github.com/mikemcdougall/REPO_NAME/pulls)

<!-- Language Statistics -->
[![GitHub top language](https://img.shields.io/github/languages/top/mikemcdougall/REPO_NAME?logo=github)](https://github.com/mikemcdougall/REPO_NAME)
[![GitHub language count](https://img.shields.io/github/languages/count/mikemcdougall/REPO_NAME?logo=github)](https://github.com/mikemcdougall/REPO_NAME)

<!-- Repository Size -->
[![GitHub repo size](https://img.shields.io/github/repo-size/mikemcdougall/REPO_NAME?logo=github)](https://github.com/mikemcdougall/REPO_NAME)
[![Lines of code](https://img.shields.io/tokei/lines/github/mikemcdougall/REPO_NAME?logo=github)](https://github.com/mikemcdougall/REPO_NAME)

<!-- Security -->
[![Security Rating](https://sonarcloud.io/api/project_badges/measure?project=mikemcdougall_REPO_NAME&metric=security_rating)](https://sonarcloud.io/dashboard?id=mikemcdougall_REPO_NAME)
[![Vulnerabilities](https://sonarcloud.io/api/project_badges/measure?project=mikemcdougall_REPO_NAME&metric=vulnerabilities)](https://sonarcloud.io/dashboard?id=mikemcdougall_REPO_NAME)
```

## Dashboard Links

### GitHub Actions Dashboard
- [honua-server Actions](https://github.com/mikemcdougall/honua-server/actions)
- [honua-core-sdk Actions](https://github.com/mikemcdougall/honua-core-sdk/actions)
- [honua-admin-tools Actions](https://github.com/mikemcdougall/honua-admin-tools/actions)
- [geospatial-grpc Actions](https://github.com/mikemcdougall/geospatial-grpc/actions)

### Package Registries
- [NuGet Packages](https://www.nuget.org/packages?q=Honua)
- [NPM Packages](https://www.npmjs.com/search?q=%40honua)
- [PyPI Packages](https://pypi.org/search/?q=honua)
- [Buf Registry](https://buf.build/mikemcdougall/geospatial-grpc)

### Coverage Reports
- [codecov.io](https://codecov.io/gh/mikemcdougall)

## Implementation Checklist

### For Each Repository

- [ ] Copy appropriate workflow file
- [ ] Configure required secrets
- [ ] Update README with badges
- [ ] Enable branch protection
- [ ] Configure deployment environments
- [ ] Test workflow execution
- [ ] Verify package publishing
- [ ] Monitor build status

### Cross-Repository Integration

- [ ] Verify package dependencies
- [ ] Test integration points
- [ ] Validate version compatibility
- [ ] Monitor deployment coordination
- [ ] Document deployment procedures