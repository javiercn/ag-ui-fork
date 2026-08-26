import { describe, it, expect, beforeAll, afterAll } from "vitest";
import { HttpAgent } from "@ag-ui/client";
import {
  EventType,
  type BaseEvent,
  type RunFinishedEvent,
} from "@ag-ui/core";
import { startStepServer, type StepServerHandle } from "../../helpers/step-server";

interface TextDeltaEvent extends BaseEvent {
  delta: string;
}

function collectText(events: BaseEvent[]): string {
  return events
    .filter((e) => e.type === EventType.TEXT_MESSAGE_CONTENT)
    .map((e) => (e as TextDeltaEvent).delta)
    .join("");
}

let server: StepServerHandle;

beforeAll(async () => {
  // Step10 preserves the model's normal function call and classifies the
  // complete update as an input_required interruption. Resume correlation
  // reconstructs a FunctionResultContent for the server-side interrupt.
  server = await startStepServer({
    step: 10,
    projectName: "InterruptsUserInput",
    port: 8110,
  });
}, 90_000);

afterAll(async () => {
  await server?.stop();
});

describe("TS HttpAgent -> C# Step10_InterruptsUserInput.Server", () => {
  it("emits an input_required interrupt and resumes with the user's response", async () => {
    const agent = new HttpAgent({
      url: `${server.baseUrl}/`,
      threadId: "step10-userinput",
      agentId: "step10-cross-language",
    });
    agent.messages = [
      { id: "u1", role: "user", content: "Please setup my account" },
    ];

    // Turn 1: expect an input_required interrupt.
    const turn1: BaseEvent[] = [];
    await agent.runAgent(
      {},
      {
        onEvent: ({ event }) => {
          turn1.push(event);
        },
      },
    );

    const finish = turn1.find((e) => e.type === EventType.RUN_FINISHED) as RunFinishedEvent;
    expect(finish).toBeDefined();
    const outcome = finish.outcome as
      | { type: "success" }
      | {
          type: "interrupt";
          interrupts: Array<{
            id: string;
            reason: string;
            message?: string;
            responseSchema?: unknown;
            toolCallId?: string;
          }>;
        };
    expect(outcome.type).toBe("interrupt");
    if (outcome.type !== "interrupt") return;
    const interrupt = outcome.interrupts[0]!;
    expect(interrupt.reason).toBe("input_required");
    expect(interrupt.message).toMatch(/username/i);
    expect(interrupt.responseSchema).toBeDefined();
    const toolStart = turn1.find(
      (event) => event.type === EventType.TOOL_CALL_START,
    ) as BaseEvent & { toolCallId: string; toolCallName: string };
    expect(toolStart.toolCallId).toBe(interrupt.toolCallId);
    expect(interrupt.id).toBe(toolStart.toolCallId);
    expect(toolStart.toolCallName).toBe("request_user_input");
    // Turn 2: send the function result through Resume using the function call ID.
    const turn2: BaseEvent[] = [];
    await agent.runAgent(
      {
        resume: [
          {
            interruptId: interrupt.id,
            status: "resolved",
            payload: "johndoe42",
          },
        ],
      },
      {
        onEvent: ({ event }) => {
          turn2.push(event);
        },
      },
    );

    const types2 = turn2.map((e) => e.type);
    expect(types2).toContain(EventType.RUN_STARTED);
    expect(types2).toContain(EventType.RUN_FINISHED);
    expect(types2).toContain(EventType.TEXT_MESSAGE_CONTENT);
    expect(collectText(turn2)).toMatch(/johndoe42/);
  });
});
