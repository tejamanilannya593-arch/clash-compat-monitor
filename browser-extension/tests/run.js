"use strict";

const assert = require("assert");
const { runChatGptAdapter, CHATGPT_PROFILE } = require("../chatgpt-adapter");
const { runGeminiAdapter, GEMINI_PROFILE } = require("../gemini-adapter");

let failures = 0;

async function test(name, action) {
  try {
    await action();
    console.log(`PASS ${name}`);
  } catch (error) {
    failures += 1;
    console.error(`FAIL ${name}: ${error.stack || error}`);
  }
}

class FakeEvent {
  constructor(type) { this.type = type; }
}

class FakeElement {
  constructor(text = "") {
    this.textContent = text;
    this.value = "";
    this.events = [];
    this.clicked = false;
    this.focused = false;
    this.ownerDocument = null;
  }
  dispatchEvent(event) { this.events.push(event.type); return true; }
  focus() { this.focused = true; }
  click() { this.clicked = true; if (this.onClick) this.onClick(); }
}

class FakeDocument {
  constructor() {
    this.elements = new Map();
    this.defaultView = { Event: FakeEvent, InputEvent: FakeEvent, KeyboardEvent: FakeEvent };
  }
  set(selector, values) {
    const list = Array.isArray(values) ? values : [values];
    for (const value of list) value.ownerDocument = this;
    this.elements.set(selector, list);
  }
  querySelector(selector) { return (this.elements.get(selector) || [])[0] || null; }
  querySelectorAll(selector) { return this.elements.get(selector) || []; }
}

function task(challenge = "CCM-A7F2") {
  return { taskId: "0123456789abcdef0123456789abcdef", challenge, prompt: `Reply with only ${challenge}` };
}

function fakePage(profile, config = {}) {
  const page = new FakeDocument();
  const replies = (config.existingReplies || []).map(text => new FakeElement(text));
  page.set(profile.replies[0], replies);
  const composer = new FakeElement();
  const send = new FakeElement();
  if (!config.missingComposer) page.set(profile.composer[0], composer);
  if (!config.missingSend) page.set(profile.send[0], send);
  if (config.signIn) page.set(profile.signIn[0], new FakeElement("Sign in"));
  if (config.captcha) page.set(profile.challenge[0], new FakeElement("Verify"));
  send.onClick = () => {
    for (const text of config.newReplies || []) replies.push(new FakeElement(text));
    if (config.errorAfterSend) page.set(profile.errors[0], new FakeElement("Service failed"));
  };
  return { page, composer, send };
}

function fastOptions(abortAt) {
  let now = 0;
  return {
    now: () => now,
    wait: async milliseconds => { now += milliseconds; },
    pollIntervalMilliseconds: 1,
    maxWaitMilliseconds: 3,
    signal: abortAt === undefined ? { aborted: false } : { get aborted() { return now >= abortAt; } }
  };
}

function assertSafeShape(result) {
  assert.deepStrictEqual(Object.keys(result).sort(),
    ["challengeMatched", "elapsedMilliseconds", "messageSent", "outcome"]);
}

(async () => {
  await test("new exact ChatGPT challenge reply passes", async () => {
    const fixture = fakePage(CHATGPT_PROFILE, { newReplies: ["  CCM-A7F2  "] });
    const result = await runChatGptAdapter(fixture.page, task(), fastOptions());
    assert.strictEqual(result.outcome, "Passed");
    assert.strictEqual(result.messageSent, true);
    assert.strictEqual(result.challengeMatched, true);
    assert.strictEqual(fixture.composer.focused, true);
    assert(fixture.composer.events.includes("input"));
    assert.strictEqual(fixture.send.clicked, true);
    assertSafeShape(result);
  });

  await test("new exact Gemini challenge reply passes", async () => {
    const fixture = fakePage(GEMINI_PROFILE, { newReplies: ["CCM-A7F2"] });
    const result = await runGeminiAdapter(fixture.page, task(), fastOptions());
    assert.strictEqual(result.outcome, "Passed");
    assertSafeShape(result);
  });

  await test("pre-existing challenge cannot pass", async () => {
    const fixture = fakePage(CHATGPT_PROFILE, { existingReplies: ["CCM-A7F2"] });
    const result = await runChatGptAdapter(fixture.page, task(), fastOptions());
    assert.strictEqual(result.outcome, "GenerationTimeout");
    assert.strictEqual(result.challengeMatched, false);
  });

  await test("partial challenge cannot pass", async () => {
    const fixture = fakePage(CHATGPT_PROFILE, { newReplies: ["CCM-A7F"] });
    const result = await runChatGptAdapter(fixture.page, task(), fastOptions());
    assert.strictEqual(result.outcome, "GenerationTimeout");
  });

  await test("challenge embedded inside a larger token cannot pass", async () => {
    const fixture = fakePage(CHATGPT_PROFILE, { newReplies: ["prefixCCM-A7F2suffix"] });
    const result = await runChatGptAdapter(fixture.page, task(), fastOptions());
    assert.strictEqual(result.outcome, "GenerationTimeout");
  });

  await test("sign-in state is reported without sending", async () => {
    const fixture = fakePage(CHATGPT_PROFILE, { signIn: true });
    const result = await runChatGptAdapter(fixture.page, task(), fastOptions());
    assert.deepStrictEqual(result, {
      outcome: "SignInRequired", messageSent: false, elapsedMilliseconds: 0, challengeMatched: false
    });
  });

  await test("captcha state is not blamed on the node", async () => {
    const fixture = fakePage(GEMINI_PROFILE, { captcha: true });
    const result = await runGeminiAdapter(fixture.page, task(), fastOptions());
    assert.strictEqual(result.outcome, "ChallengeRequired");
    assert.strictEqual(result.messageSent, false);
  });

  await test("explicit error after send is a conversation error", async () => {
    const fixture = fakePage(CHATGPT_PROFILE, { errorAfterSend: true });
    const result = await runChatGptAdapter(fixture.page, task(), fastOptions());
    assert.strictEqual(result.outcome, "ConversationError");
    assert.strictEqual(result.messageSent, true);
  });

  await test("no new reply times out after a sent message", async () => {
    const fixture = fakePage(GEMINI_PROFILE);
    const result = await runGeminiAdapter(fixture.page, task(), fastOptions());
    assert.strictEqual(result.outcome, "GenerationTimeout");
    assert.strictEqual(result.messageSent, true);
  });

  await test("cancellation stops an in-flight conversation", async () => {
    const fixture = fakePage(CHATGPT_PROFILE);
    const result = await runChatGptAdapter(fixture.page, task(), fastOptions(1));
    assert.strictEqual(result.outcome, "Cancelled");
    assert.strictEqual(result.messageSent, true);
  });

  await test("unknown composer DOM is unsupported", async () => {
    const fixture = fakePage(CHATGPT_PROFILE, { missingComposer: true });
    const result = await runChatGptAdapter(fixture.page, task(), fastOptions());
    assert.strictEqual(result.outcome, "AutomationUnsupported");
    assert.strictEqual(result.messageSent, false);
  });

  process.exitCode = failures === 0 ? 0 : 1;
})();
