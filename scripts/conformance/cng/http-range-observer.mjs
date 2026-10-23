import { createHash } from "node:crypto";

export const artifactKey = "pmtiles/cng/honua.pmtiles";
export const sha256 = bytes => createHash("sha256").update(bytes).digest("hex");

export function validateServingSource(content, source, baseUrl) {
  const expected = {
    url: `${baseUrl.replace(/\/$/, "")}/api/v1/tiles/pmtiles/${artifactKey}`,
    artifact_sha256: sha256(content), artifact_size: content.length,
    provider: "AwsS3", provider_environment: "localstack", object_key: artifactKey,
    bucket: "honua-cng-fixtures",
    publication_api_proven: false,
  };
  if (Object.entries(expected).some(([key, value]) => source[key] !== value)) {
    throw new Error("PMTiles serving receipt does not match the archive and supported server route");
  }
}

// Observe the canonical FetchSource's unchanged requests and responses. Reading a
// clone leaves the SDK's own status, ETag, length and cache behavior intact.
export function observeRangeFetch(fetchImpl, url, content) {
  const transfer = { requests: 0, range_requests: 0, full_object_downloads: 0, transferred_bytes: 0, distinct_objects: 0 };
  const responses = [];
  const fetch = async (input, init) => {
    const requestedUrl = typeof input === "string" ? input : input.url ?? String(input);
    if (requestedUrl !== url) throw new Error("PMTiles client requested an unexpected serving URL");
    const range = new Headers(init?.headers ?? input.headers).get("Range");
    transfer.requests++;
    if (range) transfer.range_requests++;
    transfer.distinct_objects = 1;
    const response = await fetchImpl(input, init);
    const body = Buffer.from(await response.clone().arrayBuffer());
    transfer.transferred_bytes += body.length;
    if (response.status === 200 || body.length === content.length) transfer.full_object_downloads++;
    const contentRange = response.headers.get("Content-Range");
    responses.push({ request_range: range, status: response.status, content_range: contentRange, bytes: body.length, sha256: sha256(body), response_url: response.url || url });
    const requested = /^bytes=(\d+)-(\d+)$/.exec(range ?? "");
    const observed = /^bytes (\d+)-(\d+)\/(\d+)$/.exec(contentRange ?? "");
    if ((response.url && response.url !== url) || response.status !== 206 || !requested || !observed ||
        Number(observed[1]) !== Number(requested[1]) ||
        Number(observed[2]) !== Math.min(Number(requested[2]), content.length - 1) ||
        Number(observed[3]) !== content.length) {
      throw new Error("Honua PMTiles response did not honor the requested byte range");
    }
    if (!body.equals(content.subarray(Number(observed[1]), Number(observed[2]) + 1))) {
      throw new Error("Honua PMTiles response bytes differ from the candidate-generated archive");
    }
    return response;
  };
  return { fetch, transfer, responses };
}
