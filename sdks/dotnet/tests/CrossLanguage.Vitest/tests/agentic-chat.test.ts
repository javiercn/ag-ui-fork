import { describe, it, expect } from "vitest";
import { HttpAgent } from "@ag-ui/client";
import { EventType, type BaseEvent } from "@ag-ui/core";
import { baseUrl } from "../helpers/dotnet-server";

interface TextDeltaEvent extends BaseEvent {
  delta: string;
}

function collectText(events: BaseEvent[]): string {
  return events
    .filter((e) => e.type === EventType.TEXT_MESSAGE_CONTENT)
    .map((e) => (e as TextDeltaEvent).delta)
    .join("");
}

async function runOnce(
  userMessage: string,
  threadId: string,
): Promise<BaseEvent[]> {
  const agent = new HttpAgent({
    url: `${baseUrl()}/agentic_chat`,
    threadId,
    agentId: "cross-language-test",
  });
  agent.messages = [{ id: `u-${threadId}`, role: "user", content: userMessage }];

  const events: BaseEvent[] = [];
  await agent.runAgent(
    {},
    {
      onEvent: ({ event }) => {
        events.push(event);
      },
    },
  );
  return events;
}

describe("TS HttpAgent → C# AG-UI server (agentic_chat)", () => {
  it("streams a text response for a simple greeting", async () => {
    const events = await runOnce("Hi, I am duaa", "t-greeting");

    const types = events.map((e) => e.type);
    // RUN_STARTED ... TEXT_MESSAGE_CONTENT ... RUN_FINISHED is the minimum
    // shape we require; the exact set of envelope events (e.g. STEP_*) is
    // an implementation detail of the C# server.
    expect(types).toContain(EventType.RUN_STARTED);
    expect(types).toContain(EventType.RUN_FINISHED);
    expect(types).toContain(EventType.TEXT_MESSAGE_CONTENT);
    expect(collectText(events)).toMatch(/Hello.*duaa/i);
  });

  it("preserves multi-turn context across messages", async () => {
    const agent = new HttpAgent({
      url: `${baseUrl()}/agentic_chat`,
      threadId: "t-multi",
      agentId: "cross-language-test",
    });

    // First turn — establish the topic.
    agent.messages = [
      { id: "u1", role: "user", content: "What is the capital of France?" },
    ];
    const first: BaseEvent[] = [];
    await agent.runAgent(
      {},
      {
        onEvent: ({ event }) => {
          first.push(event);
        },
      },
    );
    expect(collectText(first)).toMatch(/Paris/i);

    // The agent mutates its `messages` array as it runs, so the next runAgent
    // call sends the full prior history back to the C# server. This validates
    // that the server reconstructs the conversation from RunAgentInput.messages
    // identically to the TS server-starter would.
    agent.messages.push({
      id: "u2",
      role: "user",
      content: "What was my first question about?",
    });
    const second: BaseEvent[] = [];
    await agent.runAgent(
      {},
      {
        onEvent: ({ event }) => {
          second.push(event);
        },
      },
    );
    expect(collectText(second)).toMatch(/capital of France/i);
  });

  it("streams a long multi-line response intact (countdown)", async () => {
    // The LLM returns 35+ characters spread across newlines — the server
    // should chunk it into many TEXT_MESSAGE_CONTENT deltas. The TS client
    // reassembles them and the full text must match exactly.
    const events = await runOnce("Show me a counting down sequence", "t-countdown");
    const text = collectText(events);

    expect(text).toContain("counting down:");
    expect(text).toMatch(/10\s+9\s+8\s+7\s+6\s+5\s+4\s+3\s+2\s+1/);
    expect(text).toContain("\u2713");

    // Sanity: with content length ≈ 35 chars we expect more than one delta.
    const deltaCount = events.filter((e) => e.type === EventType.TEXT_MESSAGE_CONTENT).length;
    expect(deltaCount).toBeGreaterThan(1);
  });

  it("remembers user-provided facts across the same thread (name memory)", async () => {
    // Two-turn flow mirroring the dojo v1AgenticChat test: first the user
    // introduces themselves; second turn they ask the model to recall it.
    // The server must replay the full message history to the LLM.
    const agent = new HttpAgent({
      url: `${baseUrl()}/agentic_chat`,
      threadId: "t-name",
      agentId: "cross-language-test",
    });

    agent.messages = [
      { id: "u1", role: "user", content: "Hello, my name is Alex" },
    ];
    const turn1: BaseEvent[] = [];
    await agent.runAgent(
      {},
      {
        onEvent: ({ event }) => {
          turn1.push(event);
        },
      },
    );
    expect(collectText(turn1)).toMatch(/Alex/);

    agent.messages.push({
      id: "u2",
      role: "user",
      content: "What is my name?",
    });
    const turn2: BaseEvent[] = [];
    await agent.runAgent(
      {},
      {
        onEvent: ({ event }) => {
          turn2.push(event);
        },
      },
    );
    expect(collectText(turn2)).toMatch(/Alex/);
  });
});



