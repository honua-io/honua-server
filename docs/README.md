# Honua Server

Honua is a cloud-native geospatial server that speaks every major GIS protocol — ArcGIS-compatible REST, OGC APIs, WMS/WFS/WMTS, OData, vector tiles, STAC — from one container on your PostGIS database. No GDAL toolchain to install, no ETL pipelines, no Esri complexity.

## Try it in 10 minutes

```bash
git clone https://github.com/honua-io/honua-server.git
cd honua-server
docker compose up -d
docker compose ps
```

Open <http://localhost:8080/healthz/ready> in a browser and wait for `Ready`.

**[Start the quickstart →](get-started/quickstart.md)** — import a dataset and see it on a map in your browser.

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
| Connect QGIS, ArcGIS Pro, Excel, web maps, or AI agents | [Connect clients](guides/README.md#connect-clients) |
| Edit features and react to changes | [Edit data](guides/README.md#edit-data) |
| Lock down authentication and access | [Secure](guides/README.md#secure) |
| Deploy, monitor, back up, and scale | [Operating Honua](guides/operate/README.md) · [Deploy and operate](guides/README.md#deploy-and-operate) |
| **Migrate from ArcGIS Server or GeoServer** — discover services, dry-run, import with parity evidence | [Migrate from Esri or GeoServer](guides/README.md#migrate-from-esri-or-geoserver) |

## Why teams trust it

- **1117/1117 OGC CITE tests passing** across 13 conformance suites — [see the evidence](reference/compatibility/ogc-conformance.md)
- **Works with the clients you already use** — ArcGIS Pro, QGIS, Excel, Power BI, MapLibre, GDAL — [client compatibility](reference/compatibility/clients.md)

## Ecosystem

[honua-sdk-js](https://github.com/honua-io/honua-sdk-js) · [honua-sdk-dotnet](https://github.com/honua-io/honua-sdk-dotnet) · Honua Console (coming soon) · [honua-helm](https://github.com/honua-io/honua-helm) / Terraform modules (private) — see the [ecosystem overview](concepts/ecosystem.md).
