/**
 * Shared small utilities used across multiple esri-compat layer modules.
 *
 * Centralised here to avoid duplicating identical helper functions in every
 * layer file.
 */

/** Clamp an opacity value to the 0-1 range, defaulting to 1 for non-finite inputs. */
export function normalizeOpacity(opacity: number): number {
  if (!Number.isFinite(opacity)) {
    return 1;
  }
  return Math.min(Math.max(opacity, 0), 1);
}

/** Clamp an insertion index to the valid range [0, length]. */
export function normalizeInsertIndex(index: number, length: number): number {
  const sanitized = Number.isFinite(index) ? Math.trunc(index) : length;
  return Math.min(Math.max(sanitized, 0), length);
}

/** Normalise a scale value, returning 0 for undefined or non-finite inputs. */
export function normalizeScale(scale: number | undefined): number {
  if (scale === undefined || !Number.isFinite(scale)) {
    return 0;
  }
  return Math.max(0, Math.trunc(scale));
}
