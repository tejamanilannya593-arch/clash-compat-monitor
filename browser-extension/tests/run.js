"use strict";

const assert = require("assert");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const { runChatGptAdapter, CHATGPT_PROFILE } = require("../chatgpt-adapter");
const { runGeminiAdapter, GEMINI_PROFILE } = require("../gemini-adapter");
const { BrowserVerificationWorker, NATIVE_HOST } = require("../service-worker");
const { createChatGptContentHandler } = require("../content-chatgpt");
const { createGeminiContentHandler } = require("../content-gemini");

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

class FakeChromeEvent {
  constructor() { this.listeners = []; }
  addListener(listener) { this.listeners.push(listener); }
  removeListener(listener) { this.listeners = this.listeners.filter(value => value !== listener); }
  emit(...args) { return this.listeners.map(listener => listener(...args)); }
}

function fakeChrome() {
  const port = { posted: [], onMessage: new FakeChromeEvent(), onDisconnect: new FakeChromeEvent() };
  port.postMessage = message => port.posted.push(message);
  const tabs = new Map();
  const created = [];
  const removed = [];
  const sent = [];
  const timers = [];
  let nextTabId = 10;
  const runtimeMessages = new FakeChromeEvent();
  const chrome = {
    runtime: {
      id: "micoadiomajggfdfbnhjbpkbccjoldlg",
      onMessage: runtimeMessages,
      connectNative(name) { chrome.connectedHost = name; return port; }
    },
    tabs: {
      onUpdated: new FakeChromeEvent(),
      onRemoved: new FakeChromeEvent(),
      async create(properties) {
        const tab = { id: nextTabId++, status: "loading", url: properties.url };
        tabs.set(tab.id, tab);
        created.push({ id: tab.id, ...properties });
        return tab;
      },
      async get(id) { return tabs.get(id); },
      async sendMessage(id, message) {
        sent.push({ id, message });
        if (chrome.sendMessageError) throw new Error("no content script");
      },
      async remove(id) { removed.push(id); tabs.delete(id); }
    }
  };
  return {
    chrome, port, created, removed, sent, runtimeMessages, timers,
    setTimeout(callback, delay) { timers.push({ callback, delay }); return timers.length; },
    clearTimeout() { },
    complete(id) {
      const tab = tabs.get(id);
      if (tab) tab.status = "complete";
      chrome.tabs.onUpdated.emit(id, { status: "complete" }, tab);
    },
    removeByUser(id) {
      tabs.delete(id);
      chrome.tabs.onRemoved.emit(id, { isWindowClosing: false });
    }
  };
}

function nativeRun(service, suffix) {
  return {
    Type: "run",
    ProtocolVersion: 1,
    RequestId: `run-${suffix}`,
    TaskId: `${suffix}`.padStart(32, "0"),
    Service: service,
    Challenge: `CCM-${suffix}`,
    Url: service === "ChatGPT" ? "https://chatgpt.com/" : "https://gemini.google.com/app",
    Prompt: `Reply with only CCM-${suffix}`,
    ExpiresUtc: "2026-09-11T03:05:00.0000000Z"
  };
}

async function flush() {
  await new Promise(resolve => setImmediate(resolve));
  await new Promise(resolve => setImmediate(resolve));
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

  await test("manifest has only required API and host permissions", async () => {
    const manifest = JSON.parse(fs.readFileSync(path.join(__dirname, "..", "manifest.json"), "utf8"));
    assert.strictEqual(manifest.manifest_version, 3);
    assert.deepStrictEqual(manifest.permissions, ["nativeMessaging"]);
    assert.deepStrictEqual([...manifest.host_permissions].sort(), [
      "https://chatgpt.com/*", "https://gemini.google.com/*"
    ]);
    assert.strictEqual(manifest.key.length > 300, true);
    const digest = crypto.createHash("sha256").update(Buffer.from(manifest.key, "base64")).digest();
    const alphabet = "abcdefghijklmnop";
    const extensionId = [...digest.subarray(0, 16)]
      .map(value => alphabet[value >> 4] + alphabet[value & 15]).join("");
    assert.strictEqual(extensionId, "micoadiomajggfdfbnhjbpkbccjoldlg");
    const serialized = JSON.stringify(manifest);
    for (const forbidden of ["cookies", "history", "<all_urls>", "unsafe-eval", "http://"])
      assert.strictEqual(serialized.includes(forbidden), false, forbidden);
  });

  await test("worker serializes polls and binds result to its exact tab", async () => {
    const fake = fakeChrome();
    const worker = new BrowserVerificationWorker(fake.chrome, {
      browser: "Chrome", extensionVersion: "0.6.2",
      setTimeout: fake.setTimeout, clearTimeout: fake.clearTimeout
    });
    worker.start();
    assert.strictEqual(fake.chrome.connectedHost, NATIVE_HOST);
    assert.strictEqual(fake.port.posted[0].Type, "hello");
    fake.port.onMessage.emit({ Type: "ready", ProtocolVersion: 1, RequestId: fake.port.posted[0].RequestId });
    const firstPollCount = fake.port.posted.filter(value => value.Type === "poll").length;
    worker.requestPoll();
    worker.requestPoll();
    assert.strictEqual(fake.port.posted.filter(value => value.Type === "poll").length, firstPollCount);

    const run = nativeRun("ChatGPT", "abc123");
    run.RequestId = fake.port.posted.filter(value => value.Type === "poll").pop().RequestId;
    fake.port.onMessage.emit(run);
    await flush();
    assert.deepStrictEqual(fake.created[0], { id: 10, url: "https://chatgpt.com/", active: false });
    fake.complete(10);
    await flush();
    assert.strictEqual(fake.sent[0].id, 10);
    assert.strictEqual(fake.sent[0].message.task.taskId, run.TaskId);

    fake.runtimeMessages.emit({
      type: "ccm-result", taskId: run.TaskId, service: "ChatGPT", challenge: run.Challenge,
      result: { outcome: "Passed", messageSent: true, elapsedMilliseconds: 20, challengeMatched: true }
    }, { id: fake.chrome.runtime.id, tab: { id: 999 } });
    await flush();
    assert.strictEqual(fake.port.posted.some(value => value.Type === "result"), false);

    fake.runtimeMessages.emit({
      type: "ccm-result", taskId: run.TaskId, service: "ChatGPT", challenge: run.Challenge,
      result: { outcome: "Passed", messageSent: true, elapsedMilliseconds: 20, challengeMatched: true }
    }, { id: fake.chrome.runtime.id, tab: { id: 10 } });
    await flush();
    const forwarded = fake.port.posted.find(value => value.Type === "result");
    assert.strictEqual(forwarded.TaskId, run.TaskId);
    assert.strictEqual(forwarded.Outcome, "Passed");
    assert(fake.removed.includes(10));
  });

  await test("user tab closure is cancelled and every terminal tab is removed", async () => {
    const fake = fakeChrome();
    const worker = new BrowserVerificationWorker(fake.chrome, {
      browser: "Edge", extensionVersion: "0.6.2",
      setTimeout: fake.setTimeout, clearTimeout: fake.clearTimeout
    });
    worker.start();
    fake.port.onMessage.emit({ Type: "ready", ProtocolVersion: 1, RequestId: fake.port.posted[0].RequestId });
    const run = nativeRun("Gemini", "def456");
    run.RequestId = fake.port.posted.filter(value => value.Type === "poll").pop().RequestId;
    fake.port.onMessage.emit(run);
    await flush();
    fake.removeByUser(10);
    await flush();
    const cancellation = fake.port.posted.find(value => value.Type === "result");
    assert.strictEqual(cancellation.Outcome, "Cancelled");
    assert(fake.removed.includes(10));
  });

  await test("unsupported content DOM still closes the created tab", async () => {
    const fake = fakeChrome();
    const worker = new BrowserVerificationWorker(fake.chrome, {
      browser: "Chrome", extensionVersion: "0.6.2",
      setTimeout: fake.setTimeout, clearTimeout: fake.clearTimeout
    });
    worker.start();
    fake.port.onMessage.emit({ Type: "ready", ProtocolVersion: 1, RequestId: fake.port.posted[0].RequestId });
    const run = nativeRun("ChatGPT", "aaa111");
    run.RequestId = fake.port.posted.filter(value => value.Type === "poll").pop().RequestId;
    fake.port.onMessage.emit(run);
    await flush();
    fake.chrome.sendMessageError = true;
    fake.complete(10);
    await flush();
    const unsupported = fake.port.posted.find(value => value.Type === "result");
    assert.strictEqual(unsupported.Outcome, "AutomationUnsupported");
    assert(fake.removed.includes(10));
  });

  await test("disconnect retries 5 15 then 60 seconds and never opens a stale task", async () => {
    const fake = fakeChrome();
    const worker = new BrowserVerificationWorker(fake.chrome, {
      browser: "Chrome", extensionVersion: "0.6.2",
      setTimeout: fake.setTimeout, clearTimeout: fake.clearTimeout
    });
    worker.start();
    fake.port.onDisconnect.emit();
    fake.port.onMessage.emit(nativeRun("ChatGPT", "bbb222"));
    await flush();
    assert.strictEqual(fake.created.length, 0);
    assert.strictEqual(fake.timers[0].delay, 5000);
    fake.timers[0].callback();
    fake.port.onDisconnect.emit();
    assert.strictEqual(fake.timers[1].delay, 15000);
    fake.timers[1].callback();
    fake.port.onDisconnect.emit();
    assert.strictEqual(fake.timers[2].delay, 60000);
    fake.timers[2].callback();
    fake.port.onDisconnect.emit();
    assert.strictEqual(fake.timers[3].delay, 60000);
  });

  await test("content handlers stay dormant until sender task and origin pass", async () => {
    let chatRuns = 0;
    let geminiRuns = 0;
    const sent = [];
    const runtime = { id: "extension-id", sendMessage: async message => sent.push(message) };
    const chatHandler = createChatGptContentHandler({
      runtime, page: {}, location: { origin: "https://chatgpt.com" },
      adapter: async () => { chatRuns += 1; return { outcome: "Passed", messageSent: true, elapsedMilliseconds: 1, challengeMatched: true }; }
    });
    const chatTask = {
      type: "ccm-run",
      task: { taskId: "0".repeat(32), service: "ChatGPT", challenge: "CCM-A7F2", prompt: "Reply CCM-A7F2" }
    };
    chatHandler(chatTask, { id: "wrong-extension" });
    chatHandler({ ...chatTask, task: { ...chatTask.task, service: "Gemini" } }, { id: runtime.id });
    assert.strictEqual(chatRuns, 0);
    chatHandler(chatTask, { id: runtime.id });
    await flush();
    assert.strictEqual(chatRuns, 1);
    assert.strictEqual(sent[0].taskId, chatTask.task.taskId);

    const geminiHandler = createGeminiContentHandler({
      runtime, page: {}, location: { origin: "https://lookalike.invalid" },
      adapter: async () => { geminiRuns += 1; return {}; }
    });
    geminiHandler({ ...chatTask, task: { ...chatTask.task, service: "Gemini" } }, { id: runtime.id });
    await flush();
    assert.strictEqual(geminiRuns, 0);
  });

  process.exitCode = failures === 0 ? 0 : 1;
})();
