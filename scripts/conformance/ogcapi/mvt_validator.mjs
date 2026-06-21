#!/usr/bin/env node
// MVT + TileMatrixSet conformance gate for Honua Server.
//
// honua advertises the OGC API Tiles `mvt` conformance class and serves Mapbox
// Vector Tiles at /ogc/tiles/.../{z}/{x}/{y}?f=mvt, but there is no TeamEngine
// CITE suite that validates the *tile bytes* against the MVT 2.1 spec, nor the
// TMS 2.0 tile-metadata JSON. This gate uses the canonical alternatives:
//
//   1. @mapbox/vtvalidate (vtzero-based MVT 2.1 validator) on each tile buffer
//      fetched from honua. When the native vtvalidate addon is unavailable on
//      the host (e.g. no prebuilt for the running Node ABI), it falls back to a
//      structural decode with the pure-JS @mapbox/vector-tile + pbf so the gate
//      still exercises real tiles locally; CI pins a Node version with a
//      vtvalidate prebuild so the canonical validator runs there.
//   2. The vendored TMS 2.0 JSON Schemas (tileMatrixSet.json, tileSet.json) via
//      AJV against honua's /tileMatrixSets/{id} and tileset-metadata responses.
//
// Exit 0 = pass, non-zero = at least one failure.

import { readFileSync, existsSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
// TMS 2.0 schemas declare draft 2019-09, so use the matching Ajv build.
const Ajv = require("ajv/dist/2019").default ?? require("ajv/dist/2019");
const addFormats = require("ajv-formats");

const HERE = dirname(fileURLToPath(import.meta.url));
const VENDOR = join(HERE, "vendor");

// --- CLI -------------------------------------------------------------------
function parseArgs(argv) {
  const a = { base: "http://localhost:8080", collection: "0", tms: "WebMercatorQuad" };
  for (let i = 2; i < argv.length; i++) {
    const v = argv[i + 1];
    switch (argv[i]) {
      case "--base-url": a.base = v; i++; break;
      case "--collection": a.collection = v; i++; break;
      case "--tms": a.tms = v; i++; break;
      default: break;
    }
  }
  return a;
}

// --- MVT validator selection ----------------------------------------------
function loadVtValidate() {
  try {
    const vtvalidate = require("@mapbox/vtvalidate");
    return {
      name: "@mapbox/vtvalidate (MVT 2.1, vtzero)",
      validate: (buf) =>
        new Promise((resolve) => {
          vtvalidate.isValid(buf, (err, invalidReason) => {
            if (err) resolve(`vtvalidate error: ${err.message}`);
            // isValid returns a non-empty string describing the problem, or "" when valid.
            else resolve(invalidReason || null);
          });
        }),
    };
  } catch {
    // Pure-JS structural fallback: a buffer that decodes into well-formed MVT
    // layers/features without throwing is structurally valid MVT.
    const { VectorTile } = require("@mapbox/vector-tile");
    const pbf = require("pbf");
    // pbf >=4 exports { PbfReader, PbfWriter }; older versions export the
    // constructor directly. Support both.
    const Protobuf = pbf.PbfReader ?? pbf.default ?? pbf;
    return {
      name: "@mapbox/vector-tile + pbf (structural fallback)",
      validate: (buf) =>
        new Promise((resolve) => {
          try {
            const tile = new VectorTile(new Protobuf(buf));
            const layerNames = Object.keys(tile.layers);
            if (layerNames.length === 0) {
              resolve("decoded MVT has no layers");
              return;
            }
            for (const name of layerNames) {
              const layer = tile.layers[name];
              if (typeof layer.extent !== "number" || layer.extent <= 0) {
                resolve(`layer '${name}' has invalid extent ${layer.extent}`);
                return;
              }
              for (let i = 0; i < layer.length; i++) {
                const feature = layer.feature(i); // throws on malformed geometry tags
                feature.loadGeometry();
              }
            }
            resolve(null);
          } catch (e) {
            resolve(`structural decode failed: ${e.message}`);
          }
        }),
    };
  }
}

// --- HTTP helpers ----------------------------------------------------------
async function fetchBuffer(url) {
  const res = await fetch(url);
  if (res.status === 204) return { status: 204, buf: null };
  if (!res.ok) return { status: res.status, buf: null };
  const buf = Buffer.from(await res.arrayBuffer());
  return { status: res.status, buf };
}

async function fetchJson(url) {
  const res = await fetch(url, { headers: { Accept: "application/json" } });
  if (!res.ok) return { status: res.status, body: null };
  return { status: res.status, body: await res.json() };
}

// --- AJV schema validation -------------------------------------------------
// The TMS 2.0 schemas have no $id and cross-reference each other with bare
// relative filenames ("$ref": "crs.json"), so register every schema in the
// vendored directory under a key equal to its filename for ref resolution.
function makeAjv(tmsDir) {
  const ajv = new Ajv({ allErrors: true, strict: false });
  addFormats(ajv);
  // The TMS schemas use a non-standard `"format": "integer"` annotation; register
  // it as a no-op so AJV doesn't emit noise (type is already constrained).
  ajv.addFormat("integer", true);
  for (const file of readdirSync(tmsDir)) {
    if (!file.endsWith(".json")) continue;
    const schema = JSON.parse(readFileSync(join(tmsDir, file), "utf-8"));
    // Drop $schema on the proj sub-schema that pins a draft Ajv won't load.
    if (ajv.getSchema(file)) continue;
    try {
      ajv.addSchema(schema, file);
    } catch {
      // A sub-schema that can't be added (e.g. external meta-schema) is not the
      // root we validate against; refs into it would fail loudly at compile time.
    }
  }
  return ajv;
}

function validateAgainst(ajv, schemaKey, doc) {
  const validate = ajv.getSchema(schemaKey);
  if (!validate) return `schema ${schemaKey} not registered`;
  const ok = validate(doc);
  return ok ? null : ajv.errorsText(validate.errors, { separator: "\n      " });
}

// --- main ------------------------------------------------------------------
async function main() {
  const args = parseArgs(process.argv);
  const failures = [];
  let passed = 0;
  let skipped = 0;

  const validator = loadVtValidate();
  console.log(`== MVT tiles + TMS 2.0 metadata gate (${args.base}) ==`);
  console.log(`   MVT validator: ${validator.name}`);

  // 1. Validate sample tiles fetched from honua. Low-zoom tiles cover the whole
  //    world, so at least the z0/z1 tiles should carry the seeded geometry.
  const sampleTiles = [
    [0, 0, 0],
    [1, 0, 0],
    [1, 1, 0],
    [2, 1, 1],
    [3, 4, 2],
  ];
  let nonEmpty = 0;
  for (const [z, x, y] of sampleTiles) {
    const url = `${args.base}/ogc/tiles/collections/${args.collection}/tiles/${args.tms}/${z}/${x}/${y}?f=mvt`;
    const { status, buf } = await fetchBuffer(url);
    if (status === 204 || (buf && buf.length === 0)) {
      // An empty tile (no features in that pyramid cell) is a valid response.
      skipped++;
      continue;
    }
    if (status !== 200 || !buf) {
      failures.push(`tile ${z}/${x}/${y}: HTTP ${status}`);
      continue;
    }
    const reason = await validator.validate(buf);
    if (reason) {
      failures.push(`tile ${z}/${x}/${y} (${buf.length} bytes): invalid MVT: ${reason}`);
    } else {
      passed++;
      nonEmpty++;
      console.log(`   tile ${z}/${x}/${y}: valid MVT (${buf.length} bytes)`);
    }
  }
  if (nonEmpty === 0) {
    failures.push("no non-empty MVT tiles were returned by honua (cannot prove MVT output)");
  }

  // 2. TMS 2.0 tileMatrixSet.json validation
  const tmsDir = join(VENDOR, "tms-2.0");
  if (!existsSync(join(tmsDir, "tileMatrixSet.json")) || !existsSync(join(tmsDir, "tileSet.json"))) {
    failures.push("vendored TMS 2.0 schemas missing; run vendor-schemas.py");
  } else {
    const ajv = makeAjv(tmsDir);
    const tms = await fetchJson(`${args.base}/ogc/tiles/tileMatrixSets/${args.tms}`);
    if (tms.status !== 200 || !tms.body) {
      failures.push(`tileMatrixSet ${args.tms}: HTTP ${tms.status}`);
    } else {
      const err = validateAgainst(ajv, "tileMatrixSet.json", tms.body);
      if (err) failures.push(`tileMatrixSet ${args.tms} fails TMS 2.0 schema:\n      ${err}`);
      else {
        passed++;
        console.log(`   tileMatrixSet ${args.tms}: valid against TMS 2.0 tileMatrixSet.json`);
      }
    }

    // 3. TMS 2.0 tileSet.json validation against the collection tileset metadata
    const ts = await fetchJson(
      `${args.base}/ogc/tiles/collections/${args.collection}/tiles/${args.tms}`,
    );
    if (ts.status !== 200 || !ts.body) {
      failures.push(`tileset metadata ${args.collection}/${args.tms}: HTTP ${ts.status}`);
    } else {
      const err = validateAgainst(ajv, "tileSet.json", ts.body);
      if (err) failures.push(`tileset ${args.collection}/${args.tms} fails TMS 2.0 tileSet.json:\n      ${err}`);
      else {
        passed++;
        console.log(`   tileset ${args.collection}/${args.tms}: valid against TMS 2.0 tileSet.json`);
      }
    }
  }

  console.log("\n== MVT gate summary ==");
  console.log(`   passed:  ${passed}`);
  console.log(`   failed:  ${failures.length}`);
  console.log(`   skipped (empty tiles): ${skipped}`);
  if (failures.length) {
    console.log("\nFailures:");
    for (const f of failures) console.log(`  - ${f}`);
    process.exit(1);
  }
  console.log("MVT + TMS 2.0 conformance gate PASSED");
}

main().catch((e) => {
  console.error(`MVT gate crashed: ${e.stack || e.message}`);
  process.exit(2);
});
