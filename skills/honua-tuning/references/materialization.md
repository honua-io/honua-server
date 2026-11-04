# Materialization controls

Read the [configuration reference](https://honua.io/docs/reference/configuration/environment-variables/)
for `LayerPublishing__MaterializationTimeoutSeconds`,
`LayerPublishing__AsyncPublishRowThreshold` and
`LayerPublishing__MaterializationBatchSize`, including defaults, accepted ranges
and threshold boundaries. Read the
[layer publication guide](https://honua.io/docs/guides/publish/publish-layers/)
for snapshot and asynchronous publication behavior.

Choose a budget from measured evidence. Increasing it does not fix missing
permissions, unavailable storage or invalid geometry. Preserve the previous
setting and measure memory and lock pressure when changing batch size.
Changing the async threshold changes observation, not success criteria.
Read import and publication status before retrying after a timeout. Continue
from the existing source only after confirming publication was not committed.
