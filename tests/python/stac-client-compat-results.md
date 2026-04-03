# STAC Client Compatibility Evidence

- Generated at: 2026-04-03T05:48:39.992186+00:00
- Status: pass
- Base URL: http://localhost:5565
- STAC URL: http://localhost:5565/stac
- Mode: local
- Service ID: test_service
- Collection ID: 0
- Seed snapshot: client-compat-v1.sql
- Seed snapshot path: /home/makani/worktrees/honua-server/687/tests/seed/client-compat-v1.sql
- Server version: 1.0.0
- Server commit: 6093b5ff6fac9e5aa2285abe08ac01e44de18d2c
- PySTAC version: 1.14.3
- PySTAC-Client version: 0.9.0

## Summary

- Total checks: 4
- Passed: 4
- Failed: 0
- Skipped: 0

## Checks

| Test | Summary | Status | Detail |
| --- | --- | --- | --- |
| test_pystac_validates_catalog_document | PySTAC validated the raw /stac catalog document | pass | catalog_id=honua-stac-catalog |
| test_pystac_validates_collection_and_item_documents | PySTAC validated raw collection and item payloads | pass | collection_id=0 item_id=1 |
| test_pystac_client_discovers_seeded_collection | PySTAC-Client discovered the seeded collection through /stac | pass | collection_count=1 collection_id=0 |
| test_pystac_client_search_paginates_filtered_results | PySTAC-Client paged a bbox-filtered STAC search without dropping matches | pass | returned=4 names=alpha,beta,delta,gamma |
