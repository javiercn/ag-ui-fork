import { startLLMock, stopLLMock, llmockBaseUrl } from "./llmock";
import { startDotnetServer, stopDotnetServer } from "./dotnet-server";

// Vitest globalSetup contract: setup runs once before any test file; the
// returned function runs once after all files. Both LLMock and the C# server
// stay alive across the entire test run; tests share the same processes.
export default async function setup(): Promise<() => Promise<void>> {
  await startLLMock();
  await startDotnetServer({ openAiBaseUrl: llmockBaseUrl() });

  return async () => {
    await stopDotnetServer();
    await stopLLMock();
  };
}
