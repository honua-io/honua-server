import assert from "node:assert/strict";
import test from "node:test";
import { artifactKey, observeRangeFetch, sha256, validateServingSource } from "./http-range-observer.mjs";

const url = `http://honua/api/v1/tiles/pmtiles/${artifactKey}`;
const response = (body, range, status = 206) => new Response(body, { status, headers: { "Content-Range": range } });

test("observation preserves canonical fetch input and returns its unconsumed response", async () => {
  const content = Buffer.from("abcdefghij");
  const init = { headers: { Range: "bytes=2-4" } };
  const observer = observeRangeFetch(async (input, options) => {
    assert.equal(input, url);
    assert.equal(options, init);
    return response("cde", "bytes 2-4/10");
  }, url, content);
  const result = await observer.fetch(url, init);
  assert.equal(await result.text(), "cde");
  assert.deepEqual(observer.transfer, { requests: 1, range_requests: 1, full_object_downloads: 0, transferred_bytes: 3, distinct_objects: 1 });
  assert.equal(observer.responses[0].sha256, sha256(Buffer.from("cde")));
});

test("the default browser 16KiB read counts a whole small archive even with status206", async () => {
  const observer = observeRangeFetch(async () => response("small", "bytes 0-4/5"), url, Buffer.from("small"));
  await observer.fetch(url, { headers: { Range: "bytes=0-16383" } });
  assert.equal(observer.transfer.full_object_downloads, 1);
  assert.equal(observer.transfer.transferred_bytes, 5);
});

test("ignored ranges, incorrect ranges, changed bytes and foreign URLs cannot pass", async () => {
  for (const [body, range, status] of [["abcdef", "", 200], ["bc", "bytes 1-2/6", 206], ["zz", "bytes 0-1/6", 206]]) {
    const observer = observeRangeFetch(async () => response(body, range, status), url, Buffer.from("abcdef"));
    await assert.rejects(observer.fetch(url, { headers: { Range: "bytes=0-1" } }));
    assert.equal(observer.transfer.transferred_bytes, body.length);
    if (status === 200) assert.equal(observer.transfer.full_object_downloads, 1);
  }
  const observer = observeRangeFetch(() => { throw new Error("unexpected call"); }, url, Buffer.from("a"));
  await assert.rejects(observer.fetch("http://surrogate/archive", {}), /unexpected serving URL/);
  assert.equal(observer.transfer.requests, 0);
  const redirected = response("ab", "bytes 0-1/6");
  Object.defineProperty(redirected, "url", { value: "http://surrogate/archive" });
  const redirectedObserver = observeRangeFetch(async () => redirected, url, Buffer.from("abcdef"));
  await assert.rejects(redirectedObserver.fetch(url, { headers: { Range: "bytes=0-1" } }), /did not honor/);
  assert.equal(redirectedObserver.transfer.transferred_bytes, 2);
});

test("serving source binds exact artifact, supported route and emulator scope", () => {
  const content = Buffer.from("archive");
  const source = { url, artifact_sha256: sha256(content), artifact_size: content.length,
    provider: "AwsS3", provider_environment: "localstack", object_key: artifactKey, bucket: "honua-cng-fixtures", publication_api_proven: false };
  validateServingSource(content, source, "http://honua");
  for (const changed of [{ url: "http://surrogate/archive" }, { artifact_size: 88 }, { artifact_sha256: "0".repeat(64) }, { provider_environment: "aws" }]) {
    assert.throws(() => validateServingSource(content, { ...source, ...changed }, "http://honua"), /does not match/);
  }
});
