# OGC API - Features - Part 1: Core Corrigendum (v1.0.1)

## Overview
This is the official OGC standard (version 1.0.1) that defines a web API for accessing geographic feature data. Published May 11, 2022, it serves as both an OGC standard and ISO 19168-1.

## Conformance Classes (and Honua Support)

1. **Core** - Mandatory base requirements (implemented)
2. **HTML** - Web browsing and indexing (implemented)
3. **GeoJSON** - JSON-based feature encoding (implemented)
4. **GML Simple Features Level 0** - XML-based simple geometry (not claimed)
5. **GML Simple Features Level 2** - XML with complex properties (not claimed)
6. **OpenAPI 3.0** - API definition specification (implemented)

Note: GML output for feature responses is supported, but Honua does not claim the GML conformance classes.

## Core API Endpoints

| Resource | Path | Method | Status |
|----------|------|--------|--------|
| Landing Page | `/` | GET |  Implemented |
| Conformance | `/conformance` | GET |  Implemented |
| Collections | `/collections` | GET |  Implemented |
| Collection Detail | `/collections/{collectionId}` | GET |  Implemented |
| Features | `/collections/{collectionId}/items` | GET |  Implemented |
| Single Feature | `/collections/{collectionId}/items/{featureId}` | GET |  Implemented |

## Essential Requirements

### HTTP Protocol
**Requirement**: "The server SHALL conform to HTTP 1.1" and support HTTPS where applicable.
**Status**:  Implemented

### Coordinate Systems
**Requirement**: All geometries default to WGS 84 longitude/latitude unless explicitly requested otherwise.
**Status**:  Implemented

### Query Parameters
- `limit` - Controls page size (defaults and max are server-configured via `LimitsOptions.Query`) 
- `bbox` - Filters by 4 or 6 numeric coordinates 
- `datetime` - Filters by temporal range using RFC 3339 format 

### Response Requirements
- HTTP status 200 for success, 400 for invalid parameters, 404 for missing resources 
- All responses must include `self` and `alternate` links 
- Features endpoint supports pagination via `next` links 

## Missing Requirements Analysis

Based on specification review, current implementation appears compliant with core requirements.
