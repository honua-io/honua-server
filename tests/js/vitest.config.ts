/// <reference types="vitest" />
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    globals: true,
    environment: 'node',
    testTimeout: 30000,
    hookTimeout: 60000,
    include: ['**/*.test.ts'],
    exclude: ['**/node_modules/**', '**/dist/**'],
    reporters: ['verbose'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'json', 'html'],
      exclude: ['**/node_modules/**', '**/dist/**', '**/*.test.ts'],
    },
    // Sequential by default for database-backed tests
    pool: 'forks',
    poolOptions: {
      forks: {
        singleFork: true,
      },
    },
    // Environment variables for test configuration
    env: {
      HONUA_BASE_URL: process.env.HONUA_BASE_URL || 'http://localhost:5555',
      HONUA_SERVICE_ID: process.env.HONUA_SERVICE_ID || 'test_service_gw0',
      HONUA_LAYER_ID: process.env.HONUA_LAYER_ID || '1000',
    },
  },
});
