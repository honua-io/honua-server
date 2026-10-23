---
type: reference
title: "Analysis content, artifacts, and reports"
description: "The durable side of a geoprocessing run: saved analysis items and their versions, the artifacts a job produced, and the structured report it can render."
resource: "honua://capability/analytics.content"
resources:
  - "honua://capability/analytics.reporting"
---
# Analysis content, artifacts, and reports

A geoprocessing job is transient: you submit it, poll it, and fetch its result. This
surface is the durable side of the same work — an **analysis item** you can save,
version, re-run and estimate, the **artifacts** a run produced, and a **report** that
renders what happened.

It is separate from the job endpoints on purpose. `/ogc/processes/jobs/{jobId}` answers
"is this run finished"; these endpoints answer "what did we run, what came out, and what
changed since last time". For submitting and polling a run, see
[run geoprocessing](../guides/query-analyze/run-geoprocessing.md); for the operation
catalog, see [geoprocessing operations](geoprocessing-operations.md).

All routes require an authenticated caller.

## Content items

An analysis item is a saved, versioned analysis definition. Creating a version does not
run it — running is a separate, explicit call, which is what makes estimate and preview
useful before you spend the work.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/v1/analysis/content/items` | List saved analysis items. |
| `POST` | `/api/v1/analysis/content/items` | Create an item. |
| `GET` | `/api/v1/analysis/content/items/{itemId}` | Fetch one item. |
| `POST` | `/api/v1/analysis/content/items/{itemId}/versions` | Add a version. |
| `GET` | `/api/v1/analysis/content/items/{itemId}/versions/latest` | The newest version. |
| `GET` | `/api/v1/analysis/content/items/{itemId}/versions/{contentVersion}` | A specific version. |

### Acting on a version

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `.../versions/{contentVersion}/estimate` | Cost and shape of the run, without running it. |
| `POST` | `.../versions/{contentVersion}/preview` | A bounded preview of the output. |
| `POST` | `.../versions/{contentVersion}/runs` | Execute the version. |
| `POST` | `.../versions/{contentVersion}/reruns` | Execute it again against current inputs. |

`estimate` and `preview` are the two calls that are safe to make speculatively. Prefer
them over submitting a run to find out whether a run is worth submitting.

## Artifacts and job diagnosis

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/v1/analysis/artifacts/{artifactId}` | Fetch an artifact a run produced. |
| `GET` | `/api/v1/analysis/jobs/{jobId}/logs` | Execution log for a run. |
| `GET` | `/api/v1/analysis/jobs/{jobId}/failure` | Structured failure detail for a run that did not succeed. |

`/failure` is the one to read when a job ends unsuccessfully: it returns the structured
reason rather than leaving you to infer it from the log.

## Reports

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/api/v1/analysis/reports/{jobId}` | The structured analysis report for a job. |
| `GET` | `/api/v1/analysis/reports/{jobId}/render` | The same report, rendered. |

Fetch `/reports/{jobId}` when something is going to read the result; fetch `/render`
when a person is going to look at it.

## Related pages

- [Run geoprocessing](../guides/query-analyze/run-geoprocessing.md)
- [Geoprocessing operations](geoprocessing-operations.md)
- [Automate workflows](../guides/query-analyze/automate-workflows.md)
