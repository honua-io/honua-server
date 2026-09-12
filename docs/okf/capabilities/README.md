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

| Capability | Category | Edition |
| --- | --- | --- |
| [Admin Control Plane](admin.control-plane.md) | ControlPlane | Community |
| [Multi-Tenant Operation](admin.multi-tenancy.md) | ControlPlane | Enterprise |
| [Agent Operations (Validation Layer)](ai.agent-operations.md) | AI | Pro |
| [Agent Approval Workflows](ai.approval-workflows.md) | AI | Enterprise |
| [Spec Grounding Mutations](ai.grounding.md) | AI | Pro |
| [MCP Discovery & Query](ai.mcp-discovery.md) | AI | Community |
| [Spec Apply Execution](ai.spec-apply.md) | AI | Pro |
| [Spec Artifact Retrieval](ai.spec-artifacts.md) | AI | Community |
| [Dwell Trigger](alerts.dwell.md) | Alerts | Enterprise |
| [Enter/Exit Geofence Triggers](alerts.enter-exit.md) | Alerts | Pro |
| [Alert Evaluation Engine](alerts.evaluation.md) | Alerts | Pro |
| [Threshold Trigger](alerts.threshold.md) | Alerts | Enterprise |
| [Buffer Aggregate](analytics.buffer-aggregate.md) | Analytics | Pro |
| [Spatial Clustering](analytics.clustering.md) | Analytics | Pro |
| [Analysis Artifact Content](analytics.content.md) | Analytics | Community |
| [Density Binning](analytics.density.md) | Analytics | Pro |
| [H3 Aggregation](analytics.h3.md) | Analytics | Pro |
| [Line of Sight](analytics.line-of-sight.md) | Analytics | Pro |
| [Analysis Reporting](analytics.reporting.md) | Analytics | Community |
| [Slice/Volumetric Analysis](analytics.slice.md) | Analytics | Pro |
| [Spatial Join](analytics.spatial-join.md) | Analytics | Pro |
| [Sun/Shadow Analysis](analytics.sun-shadow.md) | Analytics | Pro |
| [Viewshed](analytics.viewshed.md) | Analytics | Pro |
| [Output Caching](caching.output-cache.md) | Caching | Pro |
| [Redis Distributed Cache](caching.redis.md) | Caching | Pro |
| [AWS SNS Delivery](channels.aws-sns.md) | Channels | Enterprise |
| [Azure Event Grid Delivery](channels.azure-eventgrid.md) | Channels | Enterprise |
| [Digest Delivery](channels.digest.md) | Channels | Enterprise |
| [Email Delivery](channels.email.md) | Channels | Enterprise |
| [Slack Delivery](channels.slack.md) | Channels | Enterprise |
| [Microsoft Teams Delivery](channels.teams.md) | Channels | Enterprise |
| [Webhook Delivery](channels.webhook.md) | Channels | Pro |
| [Map Collaboration Sessions](collaboration.map-sessions.md) | Collaboration | Community |
| [Demo Showcase Surfaces](demo.showcase.md) | Demo | Community |
| [Capability Manifest](discovery.capability-manifest.md) | Discovery | Community |
| [Backup Automation](dr.backup-automation.md) | DisasterRecovery | Enterprise |
| [Cache State Backup](dr.cache-backup.md) | DisasterRecovery | Enterprise |
| [Failover Playbooks](dr.failover.md) | DisasterRecovery | Enterprise |
| [RTO/RPO Reporting](dr.rto-rpo-reporting.md) | DisasterRecovery | Enterprise |
| [Branch Versioning](editing.branch-versioning.md) | Editing | Pro |
| [FeatureServer Editing](editing.featureserver-edits.md) | Editing | Pro |
| [Data Enrichment Datasets](enrichment.datasets.md) | Enrichment | Community |
| [Field Collection Forms](fieldops.forms.md) | FieldOps | Community |
| [Offline/Field Sync](fieldops.offline-sync.md) | FieldOps | Pro |
| [GeoArrow Response Format](format.geoarrow.md) | Format | Community |
| [Batch Geocoding](geocoding.batch.md) | Geocoding | Enterprise |
| [Provider Failover](geocoding.failover.md) | Geocoding | Pro |
| [Forward Geocoding](geocoding.forward.md) | Geocoding | Community |
| [Reverse Geocoding](geocoding.reverse.md) | Geocoding | Community |
| [Claims Mapping](identity.claims-mapping.md) | Identity | Enterprise |
| [mTLS Client-Certificate Authentication](identity.mtls-client-certificate.md) | Identity | Enterprise |
| [OIDC Authentication](identity.oidc.md) | Identity | Pro |
| [OIDC Multi-Provider SSO](identity.oidc-multi-provider.md) | Identity | Enterprise |
| [ArcGIS Portal Sharing Read Surface](identity.portal-sharing.md) | Identity | Community |
| [ArcGIS Portal Token Issuance](identity.portal-token.md) | Identity | Community |
| [SAML 2.0 Authentication](identity.saml.md) | Identity | Enterprise |
| [SCIM 2.0 Provisioning](identity.scim.md) | Identity | Enterprise |
| [File Import](import.file.md) | Import | Community |
| [GeoServer Import](import.geoserver.md) | Import | Enterprise |
| [GeoServices Import](import.geoservices.md) | Import | Enterprise |
| [Durable Job Runtime](jobs.durable-runtime.md) | Jobs | Community |
| [Health Checks](ops.health.md) | Ops | Community |
| [Observability](ops.observability.md) | Ops | Community |
| [Plugin/Extension SDK](plugin.sdk.md) | Extensibility | Enterprise |
| [Print Layout Templates](printing.layout-templates.md) | Printing | Pro |
| [PDF Print Output](printing.pdf-output.md) | Printing | Pro |
| [Geoprocessing Task Execution](process.geoprocessing.md) | Process | Community |
| [OGC API Processes](process.ogc-api-processes.md) | Process | Community |
| [Databricks SQL Provider](provider.databricks.md) | DataProviders | Enterprise |
| [Amazon Redshift Provider](provider.redshift.md) | DataProviders | Enterprise |
| [Snowflake Provider](provider.snowflake.md) | DataProviders | Enterprise |
| [COG Serving](raster.cloud-cog-serving.md) | Raster | Pro |
| [Cloud Storage Configuration](raster.cloud-storage-config.md) | Raster | Pro |
| [Multidimensional Coverage (NetCDF/HDF5/Zarr)](raster.multidim-coverage.md) | Raster | Pro |
| [Temporal Raster Mosaic](raster.temporal-mosaic.md) | Raster | Pro |
| [Terrain-RGB Tiles](raster.terrain-rgb.md) | Raster | Community |
| [Network Routing](routing.solve.md) | Routing | Pro |
| [CityGML/BIM Scene Ingest](scene.bim-ingest.md) | Scene | Enterprise |
| [Scene Catalog](scene.catalog.md) | Scene | Community |
| [Point Cloud Scene Ingest](scene.pointcloud-ingest.md) | Scene | Enterprise |
| [3D Tiles Scene Serving](serve.3d-tiles-scene.md) | Serve | Community |
| [Elevation Query](serve.elevation.md) | Serve | Community |
| [FeatureServer Query](serve.geoservices-featureserver.md) | Serve | Community |
| [GeocodeServer Discovery](serve.geoservices-geocodeserver.md) | Serve | Community |
| [Geometry Service](serve.geoservices-geometry-service.md) | Serve | Community |
| [ImageServer](serve.geoservices-imageserver.md) | Serve | Community |
| [MapServer](serve.geoservices-mapserver.md) | Serve | Community |
| [GeoServices REST Root](serve.geoservices-root.md) | Serve | Community |
| [VectorTileServer](serve.geoservices-vectortileserver.md) | Serve | Community |
| [gRPC (geospatial.v1)](serve.grpc.md) | Serve | Community |
| [I3S Scene Serving](serve.i3s-scene.md) | Serve | Enterprise |
| [OData v4](serve.odata.md) | Serve | Community |
| [OGC API Coverages](serve.ogc-api-coverages.md) | Serve | Community |
| [OGC API - EDR](serve.ogc-api-edr.md) | Serve | Community |
| [OGC API Features](serve.ogc-api-features.md) | Serve | Community |
| [OGC API Maps](serve.ogc-api-maps.md) | Serve | Community |
| [OGC API Records](serve.ogc-api-records.md) | Serve | Community |
| [OGC API Tiles](serve.ogc-api-tiles.md) | Serve | Community |
| [OGC SensorThings API](serve.sensorthings.md) | Serve | Community |
| [STAC API](serve.stac.md) | Serve | Community |
| [Vector Tiles (MVT/TileJSON/PMTiles)](serve.vector-tiles.md) | Serve | Community |
| [WCS 2.0.1](serve.wcs.md) | Serve | Community |
| [WFS 2.0](serve.wfs.md) | Serve | Community |
| [WMS 1.3](serve.wms.md) | Serve | Community |
| [WMTS 1.0](serve.wmts.md) | Serve | Community |
| [High-DPI Static Maps](staticmap.high-dpi.md) | StaticMap | Pro |
| [Large Static Maps](staticmap.large-dimensions.md) | StaticMap | Pro |
| [Rich Static Map Overlays](staticmap.rich-overlays.md) | StaticMap | Pro |
| [Real-Time Feature Streams](streaming.feature-subscriptions.md) | Streaming | Pro |
| [Auto-Cartographic Styling](styling.auto-suggest.md) | Styling | Pro |
| [Smart Style Defaults](styling.defaults.md) | Styling | Community |
| [OGC API Styles](styling.ogc-api-styles.md) | Styling | Community |
| [Animation API Contract](temporal.animation-api.md) | Temporal | Pro |
| [Temporal Extent Discovery](temporal.extent-discovery.md) | Temporal | Community |
| [Temporal Query Filtering](temporal.filtering.md) | Temporal | Community |
| [Temporal Histogram (Date Bins)](temporal.histogram.md) | Temporal | Pro |
| [Time-Series Tile Filtering](temporal.time-series-tiles.md) | Temporal | Pro |
