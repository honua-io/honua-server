---
type: index
title: "Honua Server"
description: "Honua is a cloud-native geospatial server that speaks every major GIS protocol — GeoServices-compatible REST, OGC APIs, WMS/WFS/WMTS, OData, vector tiles, STAC — from one container on your PostGIS database."
---
# Honua Server

Honua is a cloud-native geospatial server that speaks every major GIS protocol — GeoServices-compatible REST, OGC APIs, WMS/WFS/WMTS, OData, vector tiles, STAC — from one container on your PostGIS database. No GDAL toolchain to install, no ETL pipelines, no GeoServices complexity.

## Start with your agent

> Set up Honua in Docker on this machine.

**[Get started with AI](get-started/with-ai.md)** follows the complete journey:
check local prerequisites, install Honua, connect data, publish services, create
and style maps/apps in realtime, and publish them back to Honua. Evaluate locally
before choosing cloud infrastructure. Git integration is optional.

The [package quickstart](get-started/quickstart.md) supplies the concrete local
installation procedure. The [GIS workflow-language guide](guides/workflows/use-workflow-language.md)
explains the shared meanings and verification behind the agent's actions.

## Pick your path

| You are a… | Start here |
|---|---|
| **Developer** building apps and integrations | [Quickstart](get-started/quickstart.md) · [Make your first map](get-started/first-map.md) · [Protocols](concepts/protocols.md) |
| **Admin / operator** running the server | [Operating Honua](guides/operate/README.md) · [Deploy with Docker Compose](guides/deploy/docker-compose.md) · [Pilot onboarding runbook](guides/deploy/pilot-onboarding-runbook.md) · [Set up authentication](guides/secure/authentication.md) |
| **Analyst** consuming the data | [Connect Excel and Power BI](guides/connect/excel-power-bi.md) · [Query features](guides/query-analyze/query-features.md) · [Export data](guides/query-analyze/export-data.md) |

## What do you want to do?

| I want to… | Go to |
|---|---|
| Publish files, databases, rasters, or tiles | [Publish data](guides/README.md#publish-data) |
| Style layers and maps | [Style maps](guides/README.md#style-maps) |
| Query, analyze, and export features | [Query and analyze](guides/README.md#query-and-analyze) |
| Connect QGIS, desktop GIS client, Excel, web maps, or AI agents | [Connect clients](guides/README.md#connect-clients) |
| Edit features and react to changes | [Edit data](guides/README.md#edit-data) |
| Lock down authentication and access | [Secure](guides/README.md#secure) |
| Deploy, monitor, back up, and scale | [Operating Honua](guides/operate/README.md) · [Deploy and operate](guides/README.md#deploy-and-operate) |
| **Migrate from GeoServices REST service or GeoServer** — discover services, dry-run, import with parity evidence | [Migrate from GeoServices or GeoServer](guides/README.md#migrate-from-geoservices-or-geoserver) |

## Why teams trust it

- **Recorded OGC conformance evidence** — [current snapshot and run receipt](cite-status.md); this records the tested revision, not a new test of every later build.
- **Works with the clients you already use** — desktop GIS client, QGIS, Excel, Power BI, MapLibre, GDAL — [client compatibility](reference/compatibility/clients.md)

## Ecosystem

[Honua Studio](studio/README.md) provides realtime map/app authoring.
[Honua Console](https://github.com/honua-io/honua-console) provides administration.
See the [ecosystem overview](concepts/ecosystem.md) for SDKs and deployment tools.
