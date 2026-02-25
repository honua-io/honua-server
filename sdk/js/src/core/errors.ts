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
