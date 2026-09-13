---
type: index
title: "Capability concepts"
description: "One concept per entry in the server capability registry: what it is, which edition carries it, and how mature its surfaces are."
tags: [capability, registry, generated]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Capability concepts

Every capability the server declares, as a concept an agent can traverse. Each page's
`resource` is the stable capability key (`honua://capability/<key>`), which is the same
identity the capability matrix, the licensing registry and the route mapping use — so an
answer found here joins to the evidence without a name lookup.

These are generated from the registry by `scripts/ci/generate-capability-concepts.py`,
and they are a pure function of `docs/gis/data/capability-keys.v1.json` and
`capability-matrix.v1.json`. Edit the registry, not these pages.

The bundle contract these pages obey — frontmatter, concept types, what is generated —
is described in [Open Knowledge Format](../README.md).

Which prose pages discuss a capability is a question about the prose, so it is not baked
in here — a page rewritten elsewhere would silently stale all 117 of these. Run
`scripts/ci/generate-capability-concepts.py --report` for that view and for the
capabilities no page in the bundle names yet.

A capability appearing here is not a statement that it is generally available.
**Status** is the registry's own lifecycle value, and 22 of these are not GA:
`preview` and `experimental` capabilities carry usage restrictions stated in full on
each page. Multi-tenant operation, for one, is Preview/trial-only, is not offered as a
hosted or managed service, and is restricted by the Elastic License 2.0.

| Capability | Category | Edition | Status |
| --- | --- | --- | --- |
| [Admin Control Plane](admin.control-plane.md) | ControlPlane | Community | ga |
| [Multi-Tenant Operation](admin.multi-tenancy.md) | ControlPlane | Enterprise | **preview** |
| [Agent Operations (Validation Layer)](ai.agent-operations.md) | AI | Pro | ga |
| [Agent Approval Workflows](ai.approval-workflows.md) | AI | Enterprise | ga |
| [Spec Grounding Mutations](ai.grounding.md) | AI | Pro | ga |
| [MCP Discovery & Query](ai.mcp-discovery.md) | AI | Community | ga |
| [Spec Apply Execution](ai.spec-apply.md) | AI | Pro | ga |
| [Spec Artifact Retrieval](ai.spec-artifacts.md) | AI | Community | ga |
| [Dwell Trigger](alerts.dwell.md) | Alerts | Enterprise | **preview** |
| [Enter/Exit Geofence Triggers](alerts.enter-exit.md) | Alerts | Pro | **preview** |
| [Alert Evaluation Engine](alerts.evaluation.md) | Alerts | Pro | **preview** |
| [Threshold Trigger](alerts.threshold.md) | Alerts | Enterprise | **preview** |
| [Buffer Aggregate](analytics.buffer-aggregate.md) | Analytics | Pro | ga |
| [Spatial Clustering](analytics.clustering.md) | Analytics | Pro | ga |
| [Analysis Artifact Content](analytics.content.md) | Analytics | Community | ga |
| [Density Binning](analytics.density.md) | Analytics | Pro | ga |
| [H3 Aggregation](analytics.h3.md) | Analytics | Pro | ga |
| [Line of Sight](analytics.line-of-sight.md) | Analytics | Pro | ga |
| [Analysis Reporting](analytics.reporting.md) | Analytics | Community | ga |
| [Slice/Volumetric Analysis](analytics.slice.md) | Analytics | Pro | ga |
| [Spatial Join](analytics.spatial-join.md) | Analytics | Pro | ga |
| [Sun/Shadow Analysis](analytics.sun-shadow.md) | Analytics | Pro | ga |
| [Viewshed](analytics.viewshed.md) | Analytics | Pro | ga |
| [Output Caching](caching.output-cache.md) | Caching | Pro | ga |
| [Redis Distributed Cache](caching.redis.md) | Caching | Pro | ga |
| [AWS SNS Delivery](channels.aws-sns.md) | Channels | Enterprise | **preview** |
| [Azure Event Grid Delivery](channels.azure-eventgrid.md) | Channels | Enterprise | **preview** |
| [Digest Delivery](channels.digest.md) | Channels | Enterprise | **preview** |
| [Email Delivery](channels.email.md) | Channels | Enterprise | **preview** |
| [Slack Delivery](channels.slack.md) | Channels | Enterprise | **preview** |
| [Microsoft Teams Delivery](channels.teams.md) | Channels | Enterprise | **preview** |
| [Webhook Delivery](channels.webhook.md) | Channels | Pro | **preview** |
| [Map Collaboration Sessions](collaboration.map-sessions.md) | Collaboration | Community | ga |
| [Demo Showcase Surfaces](demo.showcase.md) | Demo | Community | ga |
| [Capability Manifest](discovery.capability-manifest.md) | Discovery | Community | ga |
| [Backup Automation](dr.backup-automation.md) | DisasterRecovery | Enterprise | ga |
| [Cache State Backup](dr.cache-backup.md) | DisasterRecovery | Enterprise | ga |
| [Failover Playbooks](dr.failover.md) | DisasterRecovery | Enterprise | ga |
| [RTO/RPO Reporting](dr.rto-rpo-reporting.md) | DisasterRecovery | Enterprise | ga |
| [Branch Versioning](editing.branch-versioning.md) | Editing | Pro | ga |
| [FeatureServer Editing](editing.featureserver-edits.md) | Editing | Pro | ga |
| [Data Enrichment Datasets](enrichment.datasets.md) | Enrichment | Community | ga |
| [Field Collection Forms](fieldops.forms.md) | FieldOps | Community | ga |
| [Offline/Field Sync](fieldops.offline-sync.md) | FieldOps | Pro | **preview** |
| [GeoArrow Response Format](format.geoarrow.md) | Format | Community | **live** |
| [Batch Geocoding](geocoding.batch.md) | Geocoding | Enterprise | ga |
| [Provider Failover](geocoding.failover.md) | Geocoding | Pro | ga |
| [Forward Geocoding](geocoding.forward.md) | Geocoding | Community | ga |
| [Reverse Geocoding](geocoding.reverse.md) | Geocoding | Community | ga |
| [Claims Mapping](identity.claims-mapping.md) | Identity | Enterprise | ga |
| [mTLS Client-Certificate Authentication](identity.mtls-client-certificate.md) | Identity | Enterprise | ga |
| [OIDC Authentication](identity.oidc.md) | Identity | Pro | ga |
| [OIDC Multi-Provider SSO](identity.oidc-multi-provider.md) | Identity | Enterprise | ga |
| [ArcGIS Portal Sharing Read Surface](identity.portal-sharing.md) | Identity | Community | ga |
| [ArcGIS Portal Token Issuance](identity.portal-token.md) | Identity | Community | ga |
| [SAML 2.0 Authentication](identity.saml.md) | Identity | Enterprise | ga |
| [SCIM 2.0 Provisioning](identity.scim.md) | Identity | Enterprise | ga |
| [File Import](import.file.md) | Import | Community | ga |
| [GeoServer Import](import.geoserver.md) | Import | Enterprise | ga |
| [GeoServices Import](import.geoservices.md) | Import | Enterprise | ga |
| [Durable Job Runtime](jobs.durable-runtime.md) | Jobs | Community | ga |
| [Health Checks](ops.health.md) | Ops | Community | ga |
| [Observability](ops.observability.md) | Ops | Community | ga |
| [Plugin/Extension SDK](plugin.sdk.md) | Extensibility | Enterprise | ga |
| [Print Layout Templates](printing.layout-templates.md) | Printing | Pro | ga |
| [PDF Print Output](printing.pdf-output.md) | Printing | Pro | ga |
| [Geoprocessing Task Execution](process.geoprocessing.md) | Process | Community | ga |
| [OGC API Processes](process.ogc-api-processes.md) | Process | Community | ga |
| [Databricks SQL Provider](provider.databricks.md) | DataProviders | Enterprise | **experimental** |
| [Amazon Redshift Provider](provider.redshift.md) | DataProviders | Enterprise | **experimental** |
| [Snowflake Provider](provider.snowflake.md) | DataProviders | Enterprise | **experimental** |
| [COG Serving](raster.cloud-cog-serving.md) | Raster | Pro | ga |
| [Cloud Storage Configuration](raster.cloud-storage-config.md) | Raster | Pro | ga |
| [Multidimensional Coverage (NetCDF/HDF5/Zarr)](raster.multidim-coverage.md) | Raster | Pro | ga |
| [Temporal Raster Mosaic](raster.temporal-mosaic.md) | Raster | Pro | ga |
| [Terrain-RGB Tiles](raster.terrain-rgb.md) | Raster | Community | ga |
| [Network Routing](routing.solve.md) | Routing | Pro | ga |
| [CityGML/BIM Scene Ingest](scene.bim-ingest.md) | Scene | Enterprise | ga |
| [Scene Catalog](scene.catalog.md) | Scene | Community | ga |
| [Point Cloud Scene Ingest](scene.pointcloud-ingest.md) | Scene | Enterprise | ga |
| [3D Tiles Scene Serving](serve.3d-tiles-scene.md) | Serve | Community | ga |
| [Elevation Query](serve.elevation.md) | Serve | Community | ga |
| [FeatureServer Query](serve.geoservices-featureserver.md) | Serve | Community | ga |
| [GeocodeServer Discovery](serve.geoservices-geocodeserver.md) | Serve | Community | ga |
| [Geometry Service](serve.geoservices-geometry-service.md) | Serve | Community | ga |
| [ImageServer](serve.geoservices-imageserver.md) | Serve | Community | **preview** |
| [MapServer](serve.geoservices-mapserver.md) | Serve | Community | ga |
| [GeoServices REST Root](serve.geoservices-root.md) | Serve | Community | ga |
| [VectorTileServer](serve.geoservices-vectortileserver.md) | Serve | Community | ga |
| [gRPC (geospatial.v1)](serve.grpc.md) | Serve | Community | ga |
| [I3S Scene Serving](serve.i3s-scene.md) | Serve | Enterprise | **experimental** |
| [OData v4](serve.odata.md) | Serve | Community | ga |
| [OGC API Coverages](serve.ogc-api-coverages.md) | Serve | Community | **preview** |
| [OGC API - EDR](serve.ogc-api-edr.md) | Serve | Community | **preview** |
| [OGC API Features](serve.ogc-api-features.md) | Serve | Community | ga |
| [OGC API Maps](serve.ogc-api-maps.md) | Serve | Community | ga |
| [OGC API Records](serve.ogc-api-records.md) | Serve | Community | ga |
| [OGC API Tiles](serve.ogc-api-tiles.md) | Serve | Community | ga |
| [OGC SensorThings API](serve.sensorthings.md) | Serve | Community | ga |
| [STAC API](serve.stac.md) | Serve | Community | ga |
| [Vector Tiles (MVT/TileJSON/PMTiles)](serve.vector-tiles.md) | Serve | Community | ga |
| [WCS 2.0.1](serve.wcs.md) | Serve | Community | ga |
| [WFS 2.0](serve.wfs.md) | Serve | Community | ga |
| [WMS 1.3](serve.wms.md) | Serve | Community | ga |
| [WMTS 1.0](serve.wmts.md) | Serve | Community | **preview** |
| [High-DPI Static Maps](staticmap.high-dpi.md) | StaticMap | Pro | ga |
| [Large Static Maps](staticmap.large-dimensions.md) | StaticMap | Pro | ga |
| [Rich Static Map Overlays](staticmap.rich-overlays.md) | StaticMap | Pro | ga |
| [Real-Time Feature Streams](streaming.feature-subscriptions.md) | Streaming | Pro | ga |
| [Auto-Cartographic Styling](styling.auto-suggest.md) | Styling | Pro | ga |
| [Smart Style Defaults](styling.defaults.md) | Styling | Community | ga |
| [OGC API Styles](styling.ogc-api-styles.md) | Styling | Community | ga |
| [Animation API Contract](temporal.animation-api.md) | Temporal | Pro | ga |
| [Temporal Extent Discovery](temporal.extent-discovery.md) | Temporal | Community | ga |
| [Temporal Query Filtering](temporal.filtering.md) | Temporal | Community | ga |
| [Temporal Histogram (Date Bins)](temporal.histogram.md) | Temporal | Pro | ga |
| [Time-Series Tile Filtering](temporal.time-series-tiles.md) | Temporal | Pro | ga |
