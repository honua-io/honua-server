#!/usr/bin/env node

import { open } from "node:fs/promises";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, resolve } from "node:path";
import { deserialize } from "flatgeobuf/lib/mjs/geojson.js";
import { PMTiles } from "pmtiles";

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

class LocalFileSource {
  constructor(path) {
    this.path = path;
  }

  getKey() {
    return this.path;
  }

  async getBytes(offset, length) {
    const handle = await open(this.path, "r");
    try {
      const buffer = Buffer.alloc(length);
      const { bytesRead } = await handle.read(buffer, 0, length, offset);
      const data = buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + bytesRead);
      return { data };
    } finally {
      await handle.close();
    }
  }
}

async function validateFlatGeobuf(path) {
  const bytes = new Uint8Array(readFileSync(path));
  let count = 0;
  for await (const feature of deserialize(bytes)) {
    if (!feature?.geometry) throw new Error("flatgeobuf-js returned a feature without geometry");
    count += 1;
  }
  if (count < 1) throw new Error("flatgeobuf-js returned zero features");
  return {
    surface: "flatgeobuf",
    operation: "feature-read",
    canonical_client: "flatgeobuf-js",
    client_version: packageVersion("flatgeobuf"),
    lane: "node-flatgeobuf",
  };
}

async function validatePmtiles(path) {
  const archive = new PMTiles(new LocalFileSource(path));
  const header = await archive.getHeader();
  const metadata = await archive.getMetadata();
  if (header.specVersion !== 3) throw new Error(`PMTiles specVersion=${header.specVersion}, expected 3`);
  if (metadata === null || typeof metadata !== "object") throw new Error("PMTiles metadata is not an object");
  // The Honua writer fixture deterministically includes z=0/x=0/y=0. Header
  // and metadata reads do not traverse the tile directory, so require an
  // actual browser-client tile lookup before certifying archive compatibility.
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
  };
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
