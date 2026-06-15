import { LLMock } from "@copilotkit/aimock";
import * as path from "node:path";
import * as fs from "node:fs/promises";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

/**
 * Port used for the LLMock OpenAI emulator. Tests assume the C# server is
 * started with OPENAI_BASE_URL pointing here. The dojo e2e suite uses 5555;
 * we use 5556 to avoid colliding with a running dojo on the same machine.
 */
export const LLMOCK_PORT = 5556;

let server: LLMock | null = null;

/**
 * Start the LLMock OpenAI emulator with deterministic fixtures matching the
 * prompts the cross-language tests will send. The fixture set is intentionally
 * minimal — we add more as we add tests. We mirror the dojo's per-chunk
 * latency so streaming behaviour resembles the real CI configuration.
 */
export async function startLLMock(): Promise<void> {
  if (server) {
    return;
  }
  server = new LLMock({ port: LLMOCK_PORT, latency: 5 });

  const fixturesDir = path.join(__dirname, "..", "fixtures");
  const files = await fs.readdir(fixturesDir).catch(() => [] as string[]);
  for (const file of files) {
    if (file.endsWith(".json")) {
      server.loadFixtureFile(path.join(fixturesDir, file));
    }
  }

  await server.start();
}

export async function stopLLMock(): Promise<void> {
  if (!server) {
    return;
  }
  await server.stop();
  server = null;
}

export function llmockBaseUrl(): string {
  return `http://localhost:${LLMOCK_PORT}/v1`;
}
