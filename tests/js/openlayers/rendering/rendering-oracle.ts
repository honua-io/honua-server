/** A marker cannot certify a line or polygon, even when its pixels have the expected color. */
export function renderingVerdict(expected: string, observed: string[], pixels: number, minimum: number): 'pass' | 'fail' {
  return observed.length > 0 && observed.every(type => type === expected)
    && Number.isFinite(pixels) && pixels >= minimum ? 'pass' : 'fail';
}
