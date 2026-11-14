import { CatalogContractFailure } from './catalog-channel-contract.mjs';

export function acceptInspectorResult(stdout, exitCode, error) {
  if (error && (error.killed || error.signal || !Number.isInteger(error.code))) {
    throw new CatalogContractFailure('inspector-invocation-failed');
  }
  const result = JSON.parse(stdout).result;
  if (!result || (exitCode !== 0 && !(exitCode === 5 && result.isError === true))) {
    throw new CatalogContractFailure('inspector-invocation-failed');
  }
  return result;
}

function fixedClientDiagnostic(stderr) {
  try {
    const line = stderr.split('\n').findLast(value => value.trimStart().startsWith('{"error":'));
    const diagnostic = JSON.parse(line).error;
    const httpStatus = Number.isInteger(diagnostic?.status) && diagnostic.status >= 100 && diagnostic.status <= 599
      ? diagnostic.status : undefined;
    const clientErrorCode = ['error', 'auth_required', 'unreachable', 'tool_error', 'schema_unportable', 'store_unavailable']
      .includes(diagnostic?.code) ? diagnostic.code : undefined;
    return { httpStatus, clientErrorCode };
  } catch {
    return {};
  }
}

function classifyDiagnosticText(message) {
  return {
    timeoutDiagnostic: /timed? ?out|timeout|abort/i.test(message),
    protocolDiagnostic: /protocol|initialize|session|method not found/i.test(message),
    transportDiagnostic: /fetch|connect|ECONN|socket|HTTP|stream/i.test(message),
    authorizationDiagnostic: /authentication|unauthori|forbidden|\b401\b|\b403\b/i.test(message),
    rateLimitDiagnostic: /429|rate.?limit|too many requests/i.test(message),
  };
}

export function classifyClientFailure(error, context = {}) {
  const message = String(error?.message ?? '');
  return { ...context,
    rpcCode: Number.isInteger(error?.code) && error.code >= -32768 && error.code < 0 ? error.code : undefined,
    ...classifyDiagnosticText(message),
    schemaDiagnostic: /schema|validation|format|ajv|invalid|Zod/i.test(message),
    packageDiagnostic: /installed.client|package|module/i.test(message),
  };
}

export function classifyInspectorFailure(stderr, error, context) {
  const toolIndex = context.extra.indexOf('--tool-name');
  return {
    method: context.method, view: context.view, tool: toolIndex < 0 ? undefined : context.extra[toolIndex + 1],
    exitCode: typeof error?.code === 'number' ? error.code : error ? undefined : 0,
    timedOut: error?.killed === true,
    ...fixedClientDiagnostic(stderr),
    ...classifyDiagnosticText(stderr),
    schemaDiagnostic: /schema|validation|format|ajv/i.test(stderr),
    interactiveDiagnostic: /elicitation|approval|input.required/i.test(stderr),
    emptyStdout: context.stdout.trim().length === 0,
  };
}
