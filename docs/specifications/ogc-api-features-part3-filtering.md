# OGC API - Features - Part 3: Filtering (v1.0)

## Overview
This OGC standard extends OGC API - Features - Part 1: Core with enhanced filtering capabilities using Common Query Language 2 (CQL2).

## Four Main Conformance Classes

### 1. **Queryables** ✅ IMPLEMENTED
**Requirement**: Defines a resource at `/collections/{collectionId}/queryables` that publishes filterable properties as JSON Schema.
**Status**: Implemented

### 2. **Queryables as Query Parameters** ✅ IMPLEMENTED
**Requirement**: Servers must support query parameters matching queryable properties for all simple-valued queryables.
**Status**: Queryable properties are accepted as query parameters and combined with other filters via AND

### 3. **Filter** ✅ IMPLEMENTED
**Requirements**:
- `filter`: Specifies the filter expression ✅
- `filter-lang`: Identifies the language (default: `cql2-text`, also supports `cql2-json`) ✅
- `filter-crs`: Specifies coordinate reference system ✅

### 4. **Features Filter** ✅ IMPLEMENTED
**Requirement**: Binds the Filter requirements class to OGC API - Features - Part 1
**Status**: Filters work on `/collections/{collectionId}/items` endpoint

## Critical Filtering Logic ✅ IMPLEMENTED
"Other filter predicates supported by the server (e.g. `bbox`, `datetime`, etc.) SHALL be logically connected with the `AND` operator when mixed in a request with the `filter` parameter."

## Remaining Implementation Items
None (full CQL2 operator coverage implemented)

## Current Implementation Status
- ✅ CQL2-Text and CQL2-JSON filtering
- ✅ Combined with bbox/datetime/queryable filters using AND logic
- ✅ Proper error handling for invalid filters
- ✅ Queryables discovery endpoint with JSON Schema
- ✅ Queryables as query parameters for simple-valued fields
- ✅ filter-crs parameter support and validation
- ⚠️ Accent-insensitive comparisons require the PostgreSQL `unaccent` extension
