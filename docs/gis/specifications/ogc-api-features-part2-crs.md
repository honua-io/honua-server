# OGC API Features Part 2: CRS Specification (v1.0.1)

## Overview
This document specifies extensions to OGC API Features Part 1: Core, enabling servers to present geometry in multiple coordinate reference systems (CRS) via URI identifiers.

## Key Requirements

### Discovery ✅ IMPLEMENTED
- **CRS Listing**: Each spatial feature collection advertises the common output CRSs Honua surfaces directly (`CRS84`, `EPSG:4326`, `EPSG:3857`, plus storage CRS when distinct)
- **Storage CRS**: Collections may declare a `storageCrs` property indicating which CRS requires no transformation

### Query Parameters ✅ IMPLEMENTED
- **`crs` Parameter**: Clients request geometry in specific CRS using this parameter; Honua also accepts additional EPSG identifiers resolved by the CRS registry/PostGIS
- **`bbox-crs` Parameter**: Declares the CRS for bounding box coordinates in requests; Honua also accepts additional EPSG identifiers resolved by the CRS registry/PostGIS

### Response Headers ✅ IMPLEMENTED
**`Content-Crs` Header**: All responses containing geometry must include this HTTP header asserting the CRS used

### Conformance Class ✅ IMPLEMENTED
- Conformance class: http://www.opengis.net/spec/ogcapi-features-2/1.0/conf/crs

## Implementation Status
- ✅ CRS conformance class added to /ogc/features/conformance
- ✅ CRS and storageCrs metadata added to collections responses
- ✅ crs and bbox-crs parameters supported with validation
- ✅ Content-Crs header added to all geometry responses