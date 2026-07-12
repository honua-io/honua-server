import Ajv2020, { type AnySchema } from 'ajv/dist/2020.js';
import { config } from '../shared/client.js';

interface McpTool {
  name: string;
  outputSchema?: AnySchema;
}

interface JsonRpcResponse {
  result?: {
    tools?: McpTool[];
  };
  error?: {
    code: number;
    message: string;
  };
}

const errorEnvelope = {
  status: 'error',
  code: 'validation_failed',
  message: 'The request is invalid.',
  requiresReauthentication: false,
  approvalRequired: false,
  policyRef: 'policy:test',
  conflictingJobId: 'job-test',
  retryable: false,
  violations: [
    {
      code: 'required',
      message: 'layerId is required.',
      fieldPath: 'layerId',
    },
  ],
  error: {
    kind: 'ValidationFailed',
    message: 'The request is invalid.',
    stepId: 'query',
    violations: [
      {
        code: 'required',
        message: 'layerId is required.',
        fieldPath: 'layerId',
      },
    ],
  },
};

describe('MCP output schemas', () => {
  it('validate the shared typed error envelope with the SDK Ajv configuration', async () => {
    const tools = await listTools();
    const toolsWithOutputSchemas = tools.filter(
      (tool): tool is McpTool & { outputSchema: AnySchema } => tool.outputSchema !== undefined,
    );

    expect(toolsWithOutputSchemas.length).toBeGreaterThan(0);

    for (const tool of toolsWithOutputSchemas) {
      // Match the MCP TypeScript SDK validator: JSON Schema draft 2020-12
      // with strict mode disabled for interoperable advertised schemas.
      const ajv = new Ajv2020({ strict: false });
      const validate = ajv.compile(tool.outputSchema);

      expect(validate(errorEnvelope), `${tool.name}: ${ajv.errorsText(validate.errors)}`).toBe(true);

      const invalidRequiredFields: Array<[string, unknown]> = [
        ['status', 'failed'],
        ['code', 400],
        ['message', false],
        ['error', 'ValidationFailed'],
      ];
      for (const [field, invalidValue] of invalidRequiredFields) {
        const invalidEnvelope = structuredClone(errorEnvelope) as Record<string, unknown>;
        invalidEnvelope[field] = invalidValue;
        expect(
          validate(invalidEnvelope),
          `${tool.name} must retain the shared error type for ${field}`,
        ).toBe(false);
      }
    }
  });
});

async function listTools(): Promise<McpTool[]> {
  const initialize = await mcpRequest({
    jsonrpc: '2.0',
    id: 1,
    method: 'initialize',
    params: {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'honua-ajv-contract-tests', version: '1.0.0' },
    },
  });
  const sessionId = initialize.response.headers.get('mcp-session-id');
  expect(sessionId).toBeTruthy();

  await mcpRequest(
    { jsonrpc: '2.0', method: 'notifications/initialized' },
    sessionId ?? undefined,
    false,
  );
  const list = await mcpRequest(
    { jsonrpc: '2.0', id: 2, method: 'tools/list' },
    sessionId ?? undefined,
  );

  expect(list.body.error).toBeUndefined();
  expect(list.body.result?.tools).toBeDefined();
  return list.body.result?.tools ?? [];
}

async function mcpRequest(
  body: object,
  sessionId?: string,
  expectJson = true,
): Promise<{ response: Response; body: JsonRpcResponse }> {
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    Accept: 'application/json',
  };
  if (config.apiKey) {
    headers['X-API-Key'] = config.apiKey;
  }
  if (sessionId) {
    headers['Mcp-Session-Id'] = sessionId;
  }

  const response = await fetch(`${config.baseUrl}/mcp`, {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
  });
  expect(response.ok).toBe(true);

  if (!expectJson) {
    return { response, body: {} };
  }

  return { response, body: (await response.json()) as JsonRpcResponse };
}
