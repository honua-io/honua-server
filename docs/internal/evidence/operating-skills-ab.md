# Operating skills paired runs

Date: 2026-11-05 PT. Model: Claude Code with `claude-opus-5-5`. One local Compose quickstart. Each task ran twice: once with only that skill installed at `.claude/skills`, and once with no skill files. Both arms received the package README and `AGENTS.md` sections 1–6. Pass or fail is an independent check of the server, not the model's own report. A wrong turn is a tool result with is_error set, or a result whose text contained a traceback, the word unauthenticated, isError set to true, or a status of error, when that attempt was retried or the run stopped.

A skill stays only when the run with it passes and the run without it fails, or both pass and the run with it has fewer wrong turns.

| Skill | Without | With | Decision |
| --- | --- | --- | --- |
| honua-install | Pass, 0 wrong turns | Pass, 0 wrong turns | Deleted |
| honua-services | Fail, stopped unauthenticated | Pass, count 2, 1 wrong turn | Kept |
| honua-studio-maps | Fail, stopped unauthenticated | Fail, stopped to ask, no draft | Deleted |
| honua-ops | Fail, three facts unknown | Pass, pin matched, 1 wrong turn | Kept |
| honua-tuning | Pass, ready 200, 1 wrong turn | Pass, ready 200, 1 wrong turn | Deleted |

## Install

Task: initialize the extracted package and prove serving readiness plus a non-empty MCP tool list.

Without the skill the agent ran `python3 quickstart.py`, got HTTP 200 from `/healthz/ready`, and listed 12 tools. With the skill it ran `--init-only` first, then `python3 quickstart.py`, and also got HTTP 200 and 12 tools. Neither run had a wrong turn. The skill did not beat the unaided run.

## Import, publish, and query

Task: ingest `places.geojson` as `skills_fix_places`, publish layer "Skills Fix Places", and count features where `population > 10000`.

Without the skill, `tools/list` on `?view=configure` succeeded without a key and `honua_ingest_dataset` returned unauthenticated. The agent stopped. Nothing was published.

With the skill, the agent registered the catalog connection, ingested the dataset, and published it. An independent `honua_query_features` with `returnCountOnly` returned count 2. One wrong turn: a truncated tool-list payload failed JSON parsing, and the agent retried.

## Studio map

Task: save a Studio map of the published layer and share it.

Without the skill the agent stopped after an unauthenticated call and created no draft. With the skill the agent also stopped and asked which rule to follow. The copy under test told it to read `secrets/honua_admin_password`, and the packaged guide in that run said not to read `secrets/`. That sentence was only in the uncommitted copy used for the run. The deleted trunk skill does not contain it. No draft and no share URL. The run with the skill did not pass.

## Release check

Task: report whether the install is co-versioned, whether `skewedIds` is empty, and whether the serving `compose-local` `observedArtifactReference` equals the server pin in `images.json`.

Without the skill the agent used the management endpoint's default view without a key, did not find `honua_platform_release_status`, and reported all three facts as unknown.

With the skill the agent used the management endpoint at `?view=operate` and reported `isCoVersioned` true, `skewedIds` empty, and a matching pin with `skewed` false. An independent call of the same tool confirmed those three facts. One wrong turn: an earlier tool list on the default view failed and the agent reconnected with the operate view.

## Materialization timeout

Task: set `LayerPublishing__MaterializationTimeoutSeconds` to 600 on the serving service and prove `/healthz/ready` returns 200. The quickstart Compose file does not forward that name from `.env`.

Without the skill the agent added the setting to the `honua` service environment and recreated that service. The rendered environment contained `600`, `.env` did not, and ready returned 200. One wrong turn: a container exec probe exited 2.

With the skill the agent added the same line and ran `python3 quickstart.py`. The container environment was `600`, `.env` was unchanged, and ready returned 200. One wrong turn: `sha256sum -c agent-files.sha256` failed because that eval directory had no packaged `skills/` tree. The skill did not beat the unaided run.

One earlier with-skill launch started in the workspace root instead of the quickstart directory. It made no change and is not a scored run.
