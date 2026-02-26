export interface UniqueValueInfoCompat {
  value: string | number;
  symbol?: unknown;
  label?: string;
  description?: string;
}

export interface UniqueValueRendererCompatOptions {
  field?: string;
  field2?: string;
  field3?: string;
  defaultSymbol?: unknown;
  defaultLabel?: string;
  uniqueValueInfos?: UniqueValueInfoCompat[];
}

export class UniqueValueRendererCompat {
  public field: string | undefined;
  public field2: string | undefined;
  public field3: string | undefined;
  public defaultSymbol: unknown;
  public defaultLabel: string | undefined;
  public uniqueValueInfos: UniqueValueInfoCompat[];

  public constructor(options: UniqueValueRendererCompatOptions = {}) {
    this.field = options.field;
    this.field2 = options.field2;
    this.field3 = options.field3;
    this.defaultSymbol = options.defaultSymbol;
    this.defaultLabel = options.defaultLabel;
    this.uniqueValueInfos = options.uniqueValueInfos ? options.uniqueValueInfos.map(cloneUniqueValueInfo) : [];
  }

  public addUniqueValueInfo(info: UniqueValueInfoCompat): void {
    this.uniqueValueInfos.push(cloneUniqueValueInfo(info));
  }

  public removeUniqueValueInfo(value: string | number): boolean {
    const index = this.uniqueValueInfos.findIndex((item) => item.value === value);
    if (index < 0) {
      return false;
    }
    this.uniqueValueInfos.splice(index, 1);
    return true;
  }

  public clone(): UniqueValueRendererCompat {
    return new UniqueValueRendererCompat(this.toJSON());
  }

  public toJSON(): UniqueValueRendererCompatOptions {
    return {
      field: this.field,
      field2: this.field2,
      field3: this.field3,
      defaultSymbol: this.defaultSymbol,
      defaultLabel: this.defaultLabel,
      uniqueValueInfos: this.uniqueValueInfos.map(cloneUniqueValueInfo),
    };
  }
}

function cloneUniqueValueInfo(info: UniqueValueInfoCompat): UniqueValueInfoCompat {
  return {
    value: info.value,
    symbol: info.symbol,
    label: info.label,
    description: info.description,
  };
}
