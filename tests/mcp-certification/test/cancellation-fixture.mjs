// Synthetic metadata for harness/consumer controls only; never candidate evidence.
export function syntheticCancellationProof(performedBy = "Official MCP TypeScript SDK") {
  return { schema: "honua.mcp-request-cancellation-proof/v1", performedBy, fixtureSourceSha: "a".repeat(40),
    seedSqlSha256: `sha256:${"d".repeat(64)}`, allocatedIdsSha256: `sha256:${"e".repeat(64)}`,
    sessionSha256: `sha256:${"f".repeat(64)}`, argumentsSha256: `sha256:${"1".repeat(64)}`,
    requestMethod: "tools/call", toolName: "honua_query_features", serviceName: "test_service", layerId: 0,
    notificationMethod: "notifications/cancelled",
    requestId: 7, notificationRequestId: 7, responseId: 7, responseCount: 1, protocolCode: -32800,
    code: "cancelled", hasResult: false, clientAbortUsed: false, originalRequestKeptOpen: true,
    wrongSession: { observedAfterMs: 601, samples: 3 }, wrongIdType: { observedAfterMs: 601, samples: 3 },
    recoveryReadMatchesFixture: true, provider: { kind: "postgres-relation-lock", tableSchema: "public", tableName: "features", blockerPid: 101, queryPid: 102,
      observedBlocked: true, ceasedBeforeBlockerRelease: true, relationLocksReleased: true,
      beforeRowsSha256: `sha256:${"2".repeat(64)}`, afterRowsSha256: `sha256:${"2".repeat(64)}` },
    cleanup: { blockerReleased: true, otherClientClosed: true, originalRequestDrained: true } };
}

export const fixture = { sourceSha: "a".repeat(40), seedSqlSha256: `sha256:${"d".repeat(64)}`,
  allocatedIdsSha256: `sha256:${"e".repeat(64)}`, serviceName: "test_service", layerId: 0,
  expected: [{ name: "alpha", id: 21, count: 1, coordinates: [-122.49, 37.71] },
    { name: "beta", id: 22, count: 2, coordinates: [-122.475, 37.72] }] };

export function queryResult() {
  return { structuredContent: { layerId: 0, returnedCount: 2, exceededTransferLimit: false,
    geojson: { type: "FeatureCollection", features: fixture.expected.map((row) => ({ type: "Feature", id: row.id,
      properties: { name: row.name, count: row.count }, geometry: { type: "Point", coordinates: row.coordinates } })) } }, content: [] };
}
