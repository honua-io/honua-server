import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { CatalogContractFailure, verifyCatalogChannels } from './catalog-channel-contract.mjs';
import { sourceSha256 } from './installed-client.mjs';
import { openStockInspector, openStockSdk } from './stock-client-adapters.mjs';

export async function runStockClientContract(kind, baseUrl, apiKey, rosters) {
  const factories = { sdk: openStockSdk, inspector: openStockInspector };
  const open = factories[kind];
  if (!open) throw new CatalogContractFailure('unknown-stock-client');
  const adapter = await open(baseUrl, apiKey);
  try {
    const receipt = await verifyCatalogChannels({ rosters, discover: adapter.discover, call: adapter.call });
    return { ...receipt, client: kind, ...adapter.metadata,
      contractSha256: await sourceSha256(new URL('./catalog-channel-contract.mjs', import.meta.url)),
      adapterSha256: await sourceSha256(new URL('./stock-client-adapters.mjs', import.meta.url)) };
  } finally {
    await adapter.close();
  }
}

async function main() {
  const roster = JSON.parse(await readFile(new URL('../../../docs/gis/data/mcp-tool-roster.v1.json', import.meta.url), 'utf8'));
  const rosters = { ...roster.views, full: [...roster.static, ...roster.projectedAdmin] };
  const receipt = await runStockClientContract(process.argv[2], process.argv[3], process.env.HONUA_MCP_STOCK_KEY, rosters);
  process.stdout.write(JSON.stringify(receipt) + '\n');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => {
    process.stderr.write((error instanceof CatalogContractFailure ? error.message : 'client-or-host-error') + '\n');
    process.exitCode = 1;
  });
}
