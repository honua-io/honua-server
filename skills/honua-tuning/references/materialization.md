# Materialization controls

| Setting | Default | Accepted values | Meaning |
| --- | --- | --- | --- |
| `LayerPublishing__MaterializationTimeoutSeconds` | 300 | 1–3600 | Snapshot copy wall-clock and command budget, in seconds. |
| `LayerPublishing__AsyncPublishRowThreshold` | 10000 | Integer >= 0 | Sources above this row count publish as a pollable job. Equality stays synchronous; zero queues nonempty tables. |
| `LayerPublishing__MaterializationBatchSize` | 5000 | Integer >= 1 | Rows copied per keyset snapshot statement within the publication transaction. |

The server validates these settings at startup. Increasing a timeout does not
fix missing permissions, unavailable storage or an invalid geometry. Changing
the async threshold changes how the operation is observed, not whether it
succeeds. A larger batch can increase memory and lock pressure; measure before
choosing it.

On a materialization budget expiry the publication transaction rolls back;
the imported table remains, and automatic import publication needs review.
Read the import and publication status before retrying. Retry publication from
the existing source only after confirming it was not already committed.
