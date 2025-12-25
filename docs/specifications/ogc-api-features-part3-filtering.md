# OGC API - Features - Part 3: Filtering (v1.0)

## Overview
This OGC standard extends OGC API - Features - Part 1: Core with enhanced filtering capabilities using Common Query Language 2 (CQL2).

## Four Main Conformance Classes

### 1. **Queryables** ✅ IMPLEMENTED
**Requirement**: Defines a resource at `/collections/{collectionId}/queryables` that publishes filterable properties as JSON Schema.
**Status**: Implemented

### 2. **Queryables as Query Parameters** ⚠️ PARTIALLY IMPLEMENTED
**Requirement**: Servers must support query parameters matching queryable properties for all simple-valued queryables.
**Status**: CQL2 filtering supports all queryable properties through filter expressions

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
1. Dynamic query parameters based on queryables (optional enhancement)
2. Full CQL2-JSON support (currently only CQL2-Text)

## Current Implementation Status
- ✅ Basic CQL2-Text filtering
- ✅ Combined with bbox/datetime filters using AND logic
- ✅ Proper error handling for invalid filters
- ✅ Queryables discovery endpoint with JSON Schema
- ✅ filter-crs parameter support and validation