# Honua Manifesto

## The customer theory

GIS today is too expensive, too complex, and too inefficient. It is often locked into proprietary stacks, hard to operate, and not cloud-native. The result is that only a few specialists can use it, and everyone else pays the price.

Honua is the antidote: GIS democratized. We bring broad compatibility, cloud-native operation, and cost-efficient infrastructure so any organization can publish and use geospatial services without the legacy tax.

## The problem we are solving

- Cost: licensing and implementation fees lock out most orgs.
- Complexity: GIS requires specialized teams and bespoke setups.
- Inefficiency: heavyweight infrastructure and slow upgrades.
- Not cloud-native: deployments do not fit modern infra patterns.
- Poor interoperability: Esri vs open standards forces either/or.

## The Honua antidote

- Broad compatibility: Esri FeatureServer + OGC API Features + OData.
- Zero switching cost: Esri import and drop-in interoperability.
- Cloud-native by design: efficient infra with optional serverless paths.
- Ops-friendly: observability, predictable upgrades, reliable rollouts.
- Cost-efficient: lean infrastructure and transparent ownership.

## Cloud-native commitments

- First-class deployment options: Docker, Kubernetes, and Terraform-based cloud deployments.
- Serverless-ready: optional deployments on AWS Lambda and Azure Functions.
- Efficient by default: scale down cleanly and avoid heavyweight operational overhead.

## The principles

- Compatibility without lock-in.
- Operational excellence by default.
- Ownership over dependency.
- Access for business users, not just GIS specialists.

## Licensing and tradeoffs

Honua uses the ELv2 license for the core. We follow an open-core model: the core remains accessible and self-hostable, while enterprise licensing funds the advanced capabilities and support that larger organizations expect.

We are explicit about the tradeoff. The enterprise tier will prioritize features aligned with EnterpriseReady patterns (SSO, RBAC, audit logs, deployment options, change management, integrations, advanced reporting, and SLA/support), while the core stays fast, compatible, and transparent.

## What this means in practice

- Esri users: keep workflows, drop the licensing burden.
- Business users: GIS through BI tools, not GIS suites.
- DevOps teams: predictable deployments, efficient infra, safe upgrades.
- ISVs: publish GIS services without punitive ASP licensing.
- Open-source moderates: a bridge between proprietary and open standards.

## The promise

- Fast time-to-value: deploy in minutes, publish in hours.
- Interoperability by default: one service, multiple standards.
- Low-ops adoption: marketplace installers + turnkey templates.
- Infra efficiency: cost-effective, cloud-native, scalable.

## The roadmap (north star, not a promise)

All your bases are belong to us. Honua’s long-term goal is a full-stack geospatial platform that spans ingestion, serving, analytics, and automation across both Esri and open standards.

Directionally, that means:

- Support for the full cloud-native geospatial protocol and format surface area (without forcing lock-in). See guide.cloudnativegeo.org.
- Raster and imagery as first-class citizens.
- Broader data backends beyond PostGIS (cloud warehouses and enterprise databases).
- Geo-events, geofencing, and real-time pipelines.
- Geo-ETL and automated publishing workflows.
- Mobile data collection that syncs with core services.
- Geoprocessing at scale.

## Why now

Cloud maturity, rising GIS licensing costs, and demand for cross-platform data access make a modern, interoperable GIS platform inevitable. Honua is that platform.
