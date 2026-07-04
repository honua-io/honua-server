# Honua Roadmap

This is the public roadmap for Honua Server. It's driven by real demand — **upvote the things you want.**

## How to upvote
- **Existing roadmap items:** react with 👍 on the issue. We sort by votes.
  → [All roadmap items, most-upvoted first](https://github.com/honua-io/honua-server/issues?q=is%3Aissue+is%3Aopen+label%3Aroadmap+sort%3Areactions-%2B1-desc)
- **New idea not listed?** Open it in [Discussions → Ideas](https://github.com/honua-io/honua-server/discussions/categories/ideas) (has an upvote button). Popular ideas graduate to roadmap issues.

Votes don't dictate order on their own, but they're the single biggest signal we use — especially for **which preview feature we finish and promote to GA next.**

---

## Shipped — GA today
The GA core is production-ready now (see [`GA_TIER_DEFINITION`](https://github.com/honua-io/honua-sales) for the full split): every protocol surface, Esri FeatureServer editing, spatial analytics, agentic operations, cloud rasters, geocoding, migration imports from ArcGIS REST + GeoServer, SSO (OIDC/SAML/SCIM) + RBAC + audit. Tracked under milestone **v1.0 (GA)**.

## Preview → finish & promote to GA (`roadmap` + built, gated experimental)
These are **implemented but gated off** the GA surface (`Capabilities:Experimental:*`, ADR-0058). Finishing each = harden + test + un-gate. **Your votes decide the order.**
- [#2427 Alerts (geofence + threshold)](https://github.com/honua-io/honua-server/issues/2427) · Next
- [#2428 Real-time feature streams](https://github.com/honua-io/honua-server/issues/2428) · Next
- [#2429 Temporal analytics](https://github.com/honua-io/honua-server/issues/2429) · Next
- [#2430 Offline / disconnected sync](https://github.com/honua-io/honua-server/issues/2430) · Later
- [#2431 mTLS client-certificate auth](https://github.com/honua-io/honua-server/issues/2431) · Later

## Now — in progress
- [#1240 ArcGIS Portal / Sharing facade + generateToken/OAuth2](https://github.com/honua-io/honua-server/issues/1240)

## Next
- [#1259 Port Esri geoprocessing (GP) services](https://github.com/honua-io/honua-server/issues/1259)
- [#1263 Geocoding: GeocodeServer compat + Esri locator import](https://github.com/honua-io/honua-server/issues/1263)
- [#1948 MCP redesign — agent-native GIS surface](https://github.com/honua-io/honua-server/issues/1948)
- [#390 Honua Cloud — AWS & Azure Marketplace SaaS](https://github.com/honua-io/honua-server/issues/390)

## Later / under consideration
- [#1273 Non-functional parity (CRS/datum fidelity, gov auth)](https://github.com/honua-io/honua-server/issues/1273)
- [#1275 Government auth: SAML, CAC/PIV, FIPS, row/field-level security](https://github.com/honua-io/honua-server/issues/1275)
- [#971 Collaborative map session transport (presence, cursors)](https://github.com/honua-io/honua-server/issues/971)
- [#1278 Studio Map multiplayer collaboration](https://github.com/honua-io/honua-server/issues/1278)
- [#374 Data enrichment API (spatial-join demographics)](https://github.com/honua-io/honua-server/issues/374)
- [#346 Multi-tenancy: schema-per-tenant isolation](https://github.com/honua-io/honua-server/issues/346)
- [#1562 Plugin/extension SDK — phase 2](https://github.com/honua-io/honua-server/issues/1562)
- [#2145 arcpy / toolbox (.tbx/.pyt) translation lane](https://github.com/honua-io/honua-server/issues/2145)
- [#2152 Esri .loc/.lox locator import](https://github.com/honua-io/honua-server/issues/2152)
- [#2241 Imagery/ML GP lane via cloud-native inference](https://github.com/honua-io/honua-server/issues/2241)

---

*Milestones (`v1.0 (GA)`, `v1.1`, `v1.2`) track release scheduling; the tiers above track priority. This file is a curated view — the [live roadmap query](https://github.com/honua-io/honua-server/issues?q=is%3Aissue+is%3Aopen+label%3Aroadmap+sort%3Areactions-%2B1-desc) is always current.*
