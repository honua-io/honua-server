#!/usr/bin/env node

import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";
import { deserialize } from "flatgeobuf/lib/mjs/geojson.js";
import { FetchSource, PMTiles } from "pmtiles";
import { flatGeobufMetadata, pmtilesMetadata } from "./artifact-metadata.mjs";
import { observeRangeFetch, validateServingSource } from "./http-range-observer.mjs";

const require = createRequire(import.meta.url);

function packageVersion(name) {
  let directory = dirname(require.resolve(name));
  while (directory !== dirname(directory)) {
    try {
      return JSON.parse(readFileSync(join(directory, "package.json"), "utf8")).version;
    } catch {
      directory = dirname(directory);
    }
  }
  throw new Error(`could not locate package.json for ${name}`);
}

async function validateFlatGeobuf(path) {
  const bytes = new Uint8Array(readFileSync(path));
  const features = [];
  let header;
  for await (const feature of deserialize(bytes, undefined, value => { header = value; })) {
    if (!feature?.geometry) throw new Error("flatgeobuf-js returned a feature without geometry");
    features.push(feature);
  }
  if (features.length < 1) throw new Error("flatgeobuf-js returned zero features");
  return {
    surface: "flatgeobuf",
    operation: "feature-read",
    canonical_client: "flatgeobuf-js",
    client_version: packageVersion("flatgeobuf"),
    lane: "node-flatgeobuf",
    observed_metadata: flatGeobufMetadata(features, header),
  };
}

async function validatePmtiles(path) {
  const content = readFileSync(path);
  const servingSource = JSON.parse(readFileSync(join(dirname(path), "pmtiles-serving-source.json"), "utf8"));
  validateServingSource(content, servingSource, process.argv[3]);
  const originalFetch = globalThis.fetch;
  const observer = observeRangeFetch(originalFetch.bind(globalThis), servingSource.url, content);
  const evidence = { serving_source: servingSource, observed_transfer: observer.transfer, http_responses: observer.responses };
  globalThis.fetch = observer.fetch;
  try {
    const archive = new PMTiles(new FetchSource(servingSource.url));
    const header = await archive.getHeader();
    const metadata = await archive.getMetadata();
    if (header.specVersion !== 3) throw new Error(`PMTiles specVersion=${header.specVersion}, expected 3`);
    if (metadata === null || typeof metadata !== "object") throw new Error("PMTiles metadata is not an object");
    // The Honua writer fixture includes z=0/x=0/y=0. Require an actual client
    // tile lookup, retaining the canonical client's normal header prefetch.
    const tile = await archive.getZxy(0, 0, 0);
    if (!tile?.data || tile.data.byteLength < 1) {
      throw new Error("PMTiles browser client returned no data for fixture tile z=0/x=0/y=0");
    }
    return {
      surface: "pmtiles",
      operation: "browser-archive-read",
      canonical_client: "PMTiles-browser-viewer",
      client_version: packageVersion("pmtiles"),
      lane: "node-pmtiles",
      observed_metadata: pmtilesMetadata(header),
      ...evidence,
    };
  } catch (error) {
    error.evidence = evidence;
    throw error;
  } finally {
    globalThis.fetch = originalFetch;
  }
}

const artifacts = resolve(process.argv[2]);

async function observe(check, identity) {
  try {
    return { ...(await check()), result: "pass" };
  } catch (error) {
    return {
      ...identity,
      result: "fail",
      failure_reason: `${error?.name ?? "Error"}: ${error?.message ?? String(error)}`,
      ...error.evidence,
    };
  }
}

const observations = await Promise.all([
  observe(
    () => validateFlatGeobuf(join(artifacts, "cng.fgb")),
    {
      surface: "flatgeobuf",
      operation: "feature-read",
      canonical_client: "flatgeobuf-js",
      client_version: packageVersion("flatgeobuf"),
      lane: "node-flatgeobuf",
    },
  ),
  observe(
    () => validatePmtiles(join(artifacts, "honua.pmtiles")),
    {
      surface: "pmtiles",
      operation: "browser-archive-read",
      canonical_client: "PMTiles-browser-viewer",
      client_version: packageVersion("pmtiles"),
      lane: "node-pmtiles",
    },
  ),
]);
process.stdout.write(`${JSON.stringify(observations)}\n`);
