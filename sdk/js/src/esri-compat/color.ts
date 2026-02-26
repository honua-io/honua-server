export type ColorCompatInput = string | number[] | Record<string, unknown>;

export class ColorCompat {
  private readonly value: ColorCompatInput;

  public constructor(input: ColorCompatInput = [0, 0, 0, 1]) {
    this.value = normalizeInput(input);
  }

  public clone(): ColorCompat {
    return new ColorCompat(this.toJSON());
  }

  public toJSON(): ColorCompatInput {
    if (Array.isArray(this.value)) {
      return [...this.value];
    }
    if (typeof this.value === "object") {
      return { ...this.value };
    }
    return this.value;
  }

  public toCss(includeAlpha = true): string {
    if (typeof this.value === "string") {
      return this.value;
    }

    if (Array.isArray(this.value)) {
      const [r = 0, g = 0, b = 0, a = 1] = this.value;
      if (includeAlpha) {
        return `rgba(${r}, ${g}, ${b}, ${a})`;
      }
      return `rgb(${r}, ${g}, ${b})`;
    }

    const record = this.value;
    const r = toFiniteNumber(record.r, 0);
    const g = toFiniteNumber(record.g, 0);
    const b = toFiniteNumber(record.b, 0);
    const a = toFiniteNumber(record.a, 1);
    if (includeAlpha) {
      return `rgba(${r}, ${g}, ${b}, ${a})`;
    }
    return `rgb(${r}, ${g}, ${b})`;
  }
}

function normalizeInput(input: ColorCompatInput): ColorCompatInput {
  if (typeof input === "string") {
    return input;
  }
  if (Array.isArray(input)) {
    return input.filter((value) => typeof value === "number" && Number.isFinite(value));
  }
  return { ...input };
}

function toFiniteNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}
