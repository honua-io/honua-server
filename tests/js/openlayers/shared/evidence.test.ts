/**
 * Unit tests for EvidenceCollector pending-attempt tracking.
 *
 * Validates that the attempt/record pairing correctly detects unmatched
 * attempts when multiple tests target the same CERT or extension ID.
 */
import { describe, it, expect } from 'vitest';
import { EvidenceCollector } from './evidence';

describe('EvidenceCollector', () => {
  // -----------------------------------------------------------------------
  // Core CERT: pending-attempt guard
  // -----------------------------------------------------------------------

  describe('core attempt guard with duplicate IDs', () => {
    it('emits fail when second attempt for same ID is not recorded', () => {
      const c = new EvidenceCollector('ogc-features');

      // Test A: attempt + record pass → matched
      c.attempt('CERT-CONN-01');
      c.record('CERT-CONN-01', 'pass', { notes: 'test A' });

      // Test B: attempt but throw before record → pending = 1
      c.attempt('CERT-CONN-01');
      // (test throws — no record call)

      const envelope = c.build();
      const result = envelope.results.find(r => r.test_case_id === 'CERT-CONN-01');
      expect(result).toBeDefined();
      expect(result!.status).toBe('fail');
      expect(result!.notes).toContain('assertion failed before evidence was recorded');
    });

    it('preserves pass when all attempts are matched by records', () => {
      const c = new EvidenceCollector('ogc-features');

      c.attempt('CERT-CONN-01');
      c.record('CERT-CONN-01', 'pass', { notes: 'test A' });

      c.attempt('CERT-CONN-01');
      c.record('CERT-CONN-01', 'pass', { notes: 'test B' });

      const envelope = c.build();
      const result = envelope.results.find(r => r.test_case_id === 'CERT-CONN-01');
      expect(result).toBeDefined();
      expect(result!.status).toBe('pass');
    });

    it('single unmatched attempt without any record emits fail', () => {
      const c = new EvidenceCollector('ogc-features');

      c.attempt('CERT-DISC-01');
      // (test throws)

      const envelope = c.build();
      const result = envelope.results.find(r => r.test_case_id === 'CERT-DISC-01');
      expect(result).toBeDefined();
      expect(result!.status).toBe('fail');
    });

    it('unattempted applicable ID emits skip', () => {
      const c = new EvidenceCollector('ogc-features');

      const envelope = c.build();
      const result = envelope.results.find(r => r.test_case_id === 'CERT-DISC-01');
      expect(result).toBeDefined();
      expect(result!.status).toBe('skip');
    });
  });

  // -----------------------------------------------------------------------
  // Extension: pending-attempt guard
  // -----------------------------------------------------------------------

  describe('extension attempt guard with duplicate IDs', () => {
    it('emits fail when second extension attempt is not recorded', () => {
      const c = new EvidenceCollector('mvt');

      // Test A: attempt + record pass → matched
      c.attemptExtension('JS-EXT-01');
      c.recordExtension('JS-EXT-01', 'pass', { notes: 'test A' });

      // Test B: attempt but throw before record → pending = 1
      c.attemptExtension('JS-EXT-01');
      // (test throws)

      const envelope = c.build();
      const ext = envelope.extensions.find(e => e.test_case_id === 'JS-EXT-01');
      expect(ext).toBeDefined();
      expect(ext!.status).toBe('fail');
      expect(ext!.notes).toContain('assertion failed before evidence was recorded');
    });

    it('preserves pass when all extension attempts are matched', () => {
      const c = new EvidenceCollector('mvt');

      c.attemptExtension('JS-EXT-01');
      c.recordExtension('JS-EXT-01', 'pass', { notes: 'test A' });

      c.attemptExtension('JS-EXT-01');
      c.recordExtension('JS-EXT-01', 'pass', { notes: 'test B' });

      const envelope = c.build();
      const ext = envelope.extensions.find(e => e.test_case_id === 'JS-EXT-01');
      expect(ext).toBeDefined();
      expect(ext!.status).toBe('pass');
    });

    it('fail-wins: record(fail) followed by unmatched attempt still fails', () => {
      const c = new EvidenceCollector('mvt');

      c.attemptExtension('JS-EXT-01');
      c.recordExtension('JS-EXT-01', 'fail', { notes: 'test A failed' });

      // Second attempt that throws
      c.attemptExtension('JS-EXT-01');

      const envelope = c.build();
      const ext = envelope.extensions.find(e => e.test_case_id === 'JS-EXT-01');
      expect(ext).toBeDefined();
      expect(ext!.status).toBe('fail');
    });
  });

  // -----------------------------------------------------------------------
  // Fail-wins precedence (pre-existing, regression guard)
  // -----------------------------------------------------------------------

  describe('fail-wins precedence', () => {
    it('core: fail is not overwritten by later pass', () => {
      const c = new EvidenceCollector('ogc-features');

      c.attempt('CERT-CONN-01');
      c.record('CERT-CONN-01', 'fail', { notes: 'first: fail' });

      c.attempt('CERT-CONN-01');
      c.record('CERT-CONN-01', 'pass', { notes: 'second: pass' });

      const envelope = c.build();
      const result = envelope.results.find(r => r.test_case_id === 'CERT-CONN-01');
      expect(result!.status).toBe('fail');
      expect(result!.notes).toBe('first: fail');
    });

    it('extension: fail is not overwritten by later pass', () => {
      const c = new EvidenceCollector('mvt');

      c.attemptExtension('JS-EXT-01');
      c.recordExtension('JS-EXT-01', 'fail', { notes: 'first: fail' });

      c.attemptExtension('JS-EXT-01');
      c.recordExtension('JS-EXT-01', 'pass', { notes: 'second: pass' });

      const envelope = c.build();
      const ext = envelope.extensions.find(e => e.test_case_id === 'JS-EXT-01');
      expect(ext!.status).toBe('fail');
      expect(ext!.notes).toBe('first: fail');
    });
  });
});
