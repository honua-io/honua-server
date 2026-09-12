---
type: index
title: "Capability concepts"
description: "One concept per entry in the server capability registry: what it is, which edition carries it, how mature its surfaces are, and which documentation pages discuss it."
tags: [capability, registry, generated]
---
<!-- GENERATED FILE - DO NOT EDIT. Regenerate with scripts/ci/generate-capability-concepts.py -->

# Capability concepts

Every capability the server declares, as a concept an agent can traverse. Each page's
`resource` is the stable capability key (`honua://capability/<key>`), which is the same
identity the capability matrix, the licensing registry and the route mapping use — so an
answer found here joins to the evidence without a name lookup.

These are generated from the registry by `scripts/ci/generate-capability-concepts.py`.
Edit the registry, not these pages.

| Capability | Edition | Documented on |
| --- | --- | ---: |
| [Admin Control Plane](admin.control-plane.md) | Community | 1 page(s) |
| [Multi-Tenant Operation](admin.multi-tenancy.md) | Enterprise | 3 page(s) |
| [Agent Operations (Validation Layer)](ai.agent-operations.md) | Pro | 1 page(s) |
| [Agent Approval Workflows](ai.approval-workflows.md) | Enterprise | 0 page(s) |
| [Spec Grounding Mutations](ai.grounding.md) | Pro | 0 page(s) |
| [MCP Discovery & Query](ai.mcp-discovery.md) | Community | 0 page(s) |
| [Spec Apply Execution](ai.spec-apply.md) | Pro | 1 page(s) |
| [Spec Artifact Retrieval](ai.spec-artifacts.md) | Community | 0 page(s) |
| [Dwell Trigger](alerts.dwell.md) | Enterprise | 0 page(s) |
| [Enter/Exit Geofence Triggers](alerts.enter-exit.md) | Pro | 0 page(s) |
| [Alert Evaluation Engine](alerts.evaluation.md) | Pro | 0 page(s) |
| [Threshold Trigger](alerts.threshold.md) | Enterprise | 0 page(s) |
| [Buffer Aggregate](analytics.buffer-aggregate.md) | Pro | 1 page(s) |
| [Spatial Clustering](analytics.clustering.md) | Pro | 0 page(s) |
| [Analysis Artifact Content](analytics.content.md) | Community | 0 page(s) |
| [Density Binning](analytics.density.md) | Pro | 2 page(s) |
| [H3 Aggregation](analytics.h3.md) | Pro | 4 page(s) |
| [Line of Sight](analytics.line-of-sight.md) | Pro | 0 page(s) |
| [Analysis Reporting](analytics.reporting.md) | Community | 0 page(s) |
| [Slice/Volumetric Analysis](analytics.slice.md) | Pro | 0 page(s) |
| [Spatial Join](analytics.spatial-join.md) | Pro | 3 page(s) |
| [Sun/Shadow Analysis](analytics.sun-shadow.md) | Pro | 1 page(s) |
| [Viewshed](analytics.viewshed.md) | Pro | 2 page(s) |
| [Output Caching](caching.output-cache.md) | Pro | 0 page(s) |
| [Redis Distributed Cache](caching.redis.md) | Pro | 2 page(s) |
| [AWS SNS Delivery](channels.aws-sns.md) | Enterprise | 0 page(s) |
| [Azure Event Grid Delivery](channels.azure-eventgrid.md) | Enterprise | 0 page(s) |
| [Digest Delivery](channels.digest.md) | Enterprise | 0 page(s) |
| [Email Delivery](channels.email.md) | Enterprise | 0 page(s) |
| [Slack Delivery](channels.slack.md) | Enterprise | 0 page(s) |
| [Microsoft Teams Delivery](channels.teams.md) | Enterprise | 0 page(s) |
| [Webhook Delivery](channels.webhook.md) | Pro | 1 page(s) |
| [Map Collaboration Sessions](collaboration.map-sessions.md) | Community | 1 page(s) |
| [Demo Showcase Surfaces](demo.showcase.md) | Community | 0 page(s) |
| [Capability Manifest](discovery.capability-manifest.md) | Community | 5 page(s) |
| [Backup Automation](dr.backup-automation.md) | Enterprise | 1 page(s) |
| [Cache State Backup](dr.cache-backup.md) | Enterprise | 1 page(s) |
| [Failover Playbooks](dr.failover.md) | Enterprise | 1 page(s) |
| [RTO/RPO Reporting](dr.rto-rpo-reporting.md) | Enterprise | 1 page(s) |
| [Branch Versioning](editing.branch-versioning.md) | Pro | 3 page(s) |
| [FeatureServer Editing](editing.featureserver-edits.md) | Pro | 2 page(s) |
| [Data Enrichment Datasets](enrichment.datasets.md) | Community | 0 page(s) |
| [Field Collection Forms](fieldops.forms.md) | Community | 0 page(s) |
| [Offline/Field Sync](fieldops.offline-sync.md) | Pro | 0 page(s) |
| [GeoArrow Response Format](format.geoarrow.md) | Community | 0 page(s) |
| [Batch Geocoding](geocoding.batch.md) | Enterprise | 0 page(s) |
| [Provider Failover](geocoding.failover.md) | Pro | 0 page(s) |
| [Forward Geocoding](geocoding.forward.md) | Community | 0 page(s) |
| [Reverse Geocoding](geocoding.reverse.md) | Community | 1 page(s) |
| [Claims Mapping](identity.claims-mapping.md) | Enterprise | 1 page(s) |
| [mTLS Client-Certificate Authentication](identity.mtls-client-certificate.md) | Enterprise | 0 page(s) |
| [OIDC Authentication](identity.oidc.md) | Pro | 1 page(s) |
| [OIDC Multi-Provider SSO](identity.oidc-multi-provider.md) | Enterprise | 0 page(s) |
| [ArcGIS Portal Sharing Read Surface](identity.portal-sharing.md) | Community | 1 page(s) |
| [ArcGIS Portal Token Issuance](identity.portal-token.md) | Community | 2 page(s) |
| [SAML 2.0 Authentication](identity.saml.md) | Enterprise | 0 page(s) |
| [SCIM 2.0 Provisioning](identity.scim.md) | Enterprise | 1 page(s) |
| [File Import](import.file.md) | Community | 4 page(s) |
| [GeoServer Import](import.geoserver.md) | Enterprise | 2 page(s) |
| [GeoServices Import](import.geoservices.md) | Enterprise | 2 page(s) |
| [Durable Job Runtime](jobs.durable-runtime.md) | Community | 0 page(s) |
| [Health Checks](ops.health.md) | Community | 4 page(s) |
| [Observability](ops.observability.md) | Community | 17 page(s) |
| [Plugin/Extension SDK](plugin.sdk.md) | Enterprise | 0 page(s) |
| [Print Layout Templates](printing.layout-templates.md) | Pro | 0 page(s) |
| [PDF Print Output](printing.pdf-output.md) | Pro | 0 page(s) |
| [Geoprocessing Task Execution](process.geoprocessing.md) | Community | 1 page(s) |
| [OGC API Processes](process.ogc-api-processes.md) | Community | 14 page(s) |
| [Databricks SQL Provider](provider.databricks.md) | Enterprise | 0 page(s) |
| [Amazon Redshift Provider](provider.redshift.md) | Enterprise | 2 page(s) |
| [Snowflake Provider](provider.snowflake.md) | Enterprise | 2 page(s) |
| [COG Serving](raster.cloud-cog-serving.md) | Pro | 2 page(s) |
| [Cloud Storage Configuration](raster.cloud-storage-config.md) | Pro | 0 page(s) |
| [Multidimensional Coverage (NetCDF/HDF5/Zarr)](raster.multidim-coverage.md) | Pro | 1 page(s) |
| [Temporal Raster Mosaic](raster.temporal-mosaic.md) | Pro | 0 page(s) |
| [Terrain-RGB Tiles](raster.terrain-rgb.md) | Community | 3 page(s) |
| [Network Routing](routing.solve.md) | Pro | 0 page(s) |
| [CityGML/BIM Scene Ingest](scene.bim-ingest.md) | Enterprise | 1 page(s) |
| [Scene Catalog](scene.catalog.md) | Community | 2 page(s) |
| [Point Cloud Scene Ingest](scene.pointcloud-ingest.md) | Enterprise | 1 page(s) |
| [3D Tiles Scene Serving](serve.3d-tiles-scene.md) | Community | 1 page(s) |
| [Elevation Query](serve.elevation.md) | Community | 1 page(s) |
| [FeatureServer Query](serve.geoservices-featureserver.md) | Community | 10 page(s) |
| [GeocodeServer Discovery](serve.geoservices-geocodeserver.md) | Community | 0 page(s) |
| [Geometry Service](serve.geoservices-geometry-service.md) | Community | 4 page(s) |
| [ImageServer](serve.geoservices-imageserver.md) | Community | 12 page(s) |
| [MapServer](serve.geoservices-mapserver.md) | Community | 22 page(s) |
| [GeoServices REST Root](serve.geoservices-root.md) | Community | 0 page(s) |
| [VectorTileServer](serve.geoservices-vectortileserver.md) | Community | 2 page(s) |
| [gRPC (geospatial.v1)](serve.grpc.md) | Community | 0 page(s) |
| [I3S Scene Serving](serve.i3s-scene.md) | Enterprise | 3 page(s) |
| [OData v4](serve.odata.md) | Community | 10 page(s) |
| [OGC API Coverages](serve.ogc-api-coverages.md) | Community | 12 page(s) |
| [OGC API - EDR](serve.ogc-api-edr.md) | Community | 3 page(s) |
| [OGC API Features](serve.ogc-api-features.md) | Community | 30 page(s) |
| [OGC API Maps](serve.ogc-api-maps.md) | Community | 5 page(s) |
| [OGC API Records](serve.ogc-api-records.md) | Community | 3 page(s) |
| [OGC API Tiles](serve.ogc-api-tiles.md) | Community | 15 page(s) |
| [OGC SensorThings API](serve.sensorthings.md) | Community | 2 page(s) |
| [STAC API](serve.stac.md) | Community | 7 page(s) |
| [Vector Tiles (MVT/TileJSON/PMTiles)](serve.vector-tiles.md) | Community | 0 page(s) |
| [WCS 2.0.1](serve.wcs.md) | Community | 9 page(s) |
| [WFS 2.0](serve.wfs.md) | Community | 15 page(s) |
| [WMS 1.3](serve.wms.md) | Community | 8 page(s) |
| [WMTS 1.0](serve.wmts.md) | Community | 7 page(s) |
| [High-DPI Static Maps](staticmap.high-dpi.md) | Pro | 0 page(s) |
| [Large Static Maps](staticmap.large-dimensions.md) | Pro | 0 page(s) |
| [Rich Static Map Overlays](staticmap.rich-overlays.md) | Pro | 0 page(s) |
| [Real-Time Feature Streams](streaming.feature-subscriptions.md) | Pro | 0 page(s) |
| [Auto-Cartographic Styling](styling.auto-suggest.md) | Pro | 1 page(s) |
| [Smart Style Defaults](styling.defaults.md) | Community | 0 page(s) |
| [OGC API Styles](styling.ogc-api-styles.md) | Community | 6 page(s) |
| [Animation API Contract](temporal.animation-api.md) | Pro | 0 page(s) |
| [Temporal Extent Discovery](temporal.extent-discovery.md) | Community | 0 page(s) |
| [Temporal Query Filtering](temporal.filtering.md) | Community | 0 page(s) |
| [Temporal Histogram (Date Bins)](temporal.histogram.md) | Pro | 2 page(s) |
| [Time-Series Tile Filtering](temporal.time-series-tiles.md) | Pro | 1 page(s) |

**51 of 117 capabilities are named by no page in the bundle.** That is the documentation backlog, stated rather than inferred.
