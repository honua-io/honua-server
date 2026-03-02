import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    exclude: ["dist/**"],
  },
  coverage: {
    provider: "v8",
    reporter: ["text", "lcov"],
    include: ["src/**/*.ts"],
    thresholds: {
      lines: 80,
      functions: 80,
      branches: 70,
      statements: 80,
    },
  },
});
