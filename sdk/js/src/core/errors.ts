export class HonuaHttpError extends Error {
  public readonly statusCode: number;
  public readonly body: unknown;

  public constructor(statusCode: number, message: string, body: unknown) {
    super(`HTTP ${statusCode}: ${message}`);
    this.name = "HonuaHttpError";
    this.statusCode = statusCode;
    this.body = body;
  }
}

/** Thrown when a request exceeds the configured timeout. */
export class HonuaTimeoutError extends Error {
  public readonly timeoutMs: number;

  public constructor(timeoutMs: number) {
    super(`Request timed out after ${timeoutMs}ms`);
    this.name = "HonuaTimeoutError";
    this.timeoutMs = timeoutMs;
  }
}

/** Thrown when a network-level failure occurs (DNS, connection refused, etc.). */
export class HonuaNetworkError extends Error {
  public override readonly cause: unknown;

  public constructor(message: string, cause: unknown) {
    super(message);
    this.name = "HonuaNetworkError";
    this.cause = cause;
  }
}

/** Thrown when a request is aborted via a caller-provided AbortSignal. */
export class HonuaAbortError extends Error {
  public constructor(message = "Request was aborted") {
    super(message);
    this.name = "HonuaAbortError";
  }
}

/** Thrown when a gRPC-Web request fails, wrapping the underlying ConnectError. */
export class HonuaGrpcError extends Error {
  public readonly code: number;
  public readonly details: unknown;

  public constructor(code: number, message: string, details?: unknown) {
    super(message);
    this.name = "HonuaGrpcError";
    this.code = code;
    this.details = details;
  }
}

/** All Honua SDK error types that can be discriminated with `instanceof`. */
export type HonuaError = HonuaHttpError | HonuaTimeoutError | HonuaNetworkError | HonuaAbortError | HonuaGrpcError;

/** Type guard that narrows any value to one of the Honua SDK error types. */
export function isHonuaError(error: unknown): error is HonuaError {
  return (
    error instanceof HonuaHttpError ||
    error instanceof HonuaTimeoutError ||
    error instanceof HonuaNetworkError ||
    error instanceof HonuaAbortError ||
    error instanceof HonuaGrpcError
  );
}
