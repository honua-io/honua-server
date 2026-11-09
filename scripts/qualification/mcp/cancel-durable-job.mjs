// This adapter proves installed-client acknowledgement only. The enclosing
// lifecycle harness independently proves native cessation and absence of output.
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { discoverInstalledTools, openInstalledClient } from './installed-client.mjs';
export { CLIENT_SOURCE, SDK_VERSION } from './installed-client.mjs';

function acknowledgement(result, jobId) {
  if (result.isError !== undefined && result.isError !== false) throw new Error('tool-error');
  const structured = result.structuredContent;
  const text = result.content?.filter((item) => item.type === 'text').map((item) => JSON.parse(item.text));
  const outputs = [structured, ...(text ?? [])].filter((value) => value !== undefined);
  if (!outputs.length || outputs.some((value) => value?.jobId !== jobId
    || value.status !== 'cancellation_requested' || value.cancellationRequested !== true
    || value.result !== undefined || value.results !== undefined)) throw new Error('invalid-acknowledgement');
  return { jobId, status: 'cancellation_requested', cancellationRequested: true };
}

export async function cancelDurableJob({ baseUrl, apiKey, jobId }) {
  if (!/^[A-Za-z0-9_-]{1,128}$/.test(jobId)) throw new Error('invalid-owned-target');
  const { client, metadata } = await openInstalledClient({ baseUrl, apiKey, name: 'honua-gp-lifecycle-cancellation' });
  try {
    // Public discovery must expose the actual tool; a scripted direct JSON-RPC
    // request or HTTP success does not establish installed-client support.
    const tools = await discoverInstalledTools(client);
    const found = tools.some(tool => tool.name === 'honua_cancel_job'
      && tool.inputSchema?.required?.includes('jobId') && tool.inputSchema.properties?.jobId?.type === 'string');
    if (!found) throw new Error('missing-cancellation-tool');
    const responses = [];
    for (let attempt = 0; attempt < 2; attempt++) {
      const response = await client.callTool({ name: 'honua_cancel_job', arguments: { jobId } }, undefined, { timeout: 5_000 });
      responses.push(acknowledgement(response, jobId));
    }
    return { schema: 'honua.gp-mcp-cancellation-ack/v1', ...metadata,
      adapterSha256: createHash('sha256').update(await readFile(new URL(import.meta.url))).digest('hex'),
      jobId, tool: 'honua_cancel_job', responses, executionCessationProven: false };
  } finally {
    await client.close();
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const result = await cancelDurableJob({ baseUrl: process.argv[2], jobId: process.argv[3], apiKey: process.env.HONUA_GP_MCP_API_KEY });
    process.stdout.write(JSON.stringify(result) + '\n');
  } catch {
    // Do not echo server responses, credentials, URLs or provider diagnostics.
    process.stderr.write('Installed MCP durable-job cancellation acknowledgement failed.\n');
    process.exitCode = 1;
  }
}
