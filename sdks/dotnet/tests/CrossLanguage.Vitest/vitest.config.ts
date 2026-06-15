import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    globals: true,
    include: ["tests/**/*.test.ts"],
    // The C# server takes several seconds to start and aimock fixtures play
    // back small per-chunk delays; give individual tests enough budget for
    // the full LLM round-trip plus AG-UI streaming.
    testTimeout: 30_000,
    hookTimeout: 60_000,
    globalSetup: ["./helpers/global-setup.ts"],
  },
});


