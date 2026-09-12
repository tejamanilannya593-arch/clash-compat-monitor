(function (root, factory) {
  "use strict";
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  else {
    root.CCMBrowserWorker = api;
    new api.BrowserVerificationWorker(root.chrome).start();
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  "use strict";

  const NATIVE_HOST = "com.clashcompatibilitymonitor.browser";
  const PROTOCOL_VERSION = 1;
  const EXTENSION_VERSION = "0.6.2";
  const OUTCOMES = new Set([
    "Passed", "ConversationError", "GenerationTimeout", "SignInRequired",
    "ChallengeRequired", "AutomationUnsupported", "Cancelled"
  ]);

  function browserName() {
    const agent = typeof navigator === "object" ? navigator.userAgent || "" : "";
    return /Edg\//.test(agent) ? "Edge" : "Chrome";
  }

  function validToken(value, maximum) {
    return typeof value === "string" && value.length > 0 && value.length <= maximum && /^[A-Za-z0-9_.-]+$/.test(value);
  }

  function validTask(message) {
    if (!message || message.Type !== "run" || message.ProtocolVersion !== PROTOCOL_VERSION ||
        !/^[0-9a-f]{32}$/.test(message.TaskId || "") ||
        !/^CCM-[A-Za-z0-9-]{1,76}$/.test(message.Challenge || "") ||
        typeof message.Prompt !== "string" || message.Prompt.length === 0 || message.Prompt.length > 300)
      return false;
    return (message.Service === "ChatGPT" && message.Url === "https://chatgpt.com/") ||
      (message.Service === "Gemini" && message.Url === "https://gemini.google.com/app");
  }

  function validContentResult(message, active) {
    const result = message && message.result;
    return message && message.type === "ccm-result" && message.taskId === active.task.taskId &&
      message.service === active.task.service && message.challenge === active.task.challenge &&
      result && OUTCOMES.has(result.outcome) && typeof result.messageSent === "boolean" &&
      Number.isFinite(result.elapsedMilliseconds) && result.elapsedMilliseconds >= 0 &&
      result.elapsedMilliseconds <= 300000 && typeof result.challengeMatched === "boolean" &&
      (result.outcome !== "Passed" || result.challengeMatched);
  }

  class BrowserVerificationWorker {
    constructor(chromeApi, options) {
      if (!chromeApi || !chromeApi.runtime || !chromeApi.tabs) throw new Error("Chrome extension APIs are required.");
      options = options || {};
      this.chrome = chromeApi;
      this.browser = options.browser || browserName();
      this.extensionVersion = options.extensionVersion || EXTENSION_VERSION;
      this.setTimer = options.setTimeout || setTimeout;
      this.clearTimer = options.clearTimeout || clearTimeout;
      this.port = null;
      this.connected = false;
      this.requestInFlight = false;
      this.pendingRequestId = null;
      this.sequence = 0;
      this.retryIndex = 0;
      this.retryTimer = null;
      this.active = null;
      this.started = false;
      this.onRuntimeMessage = this.onRuntimeMessage.bind(this);
      this.onTabUpdated = this.onTabUpdated.bind(this);
      this.onTabRemoved = this.onTabRemoved.bind(this);
    }

    start() {
      if (this.started) return;
      this.started = true;
      this.chrome.runtime.onMessage.addListener(this.onRuntimeMessage);
      this.chrome.tabs.onUpdated.addListener(this.onTabUpdated);
      this.chrome.tabs.onRemoved.addListener(this.onTabRemoved);
      this.connect();
    }

    connect() {
      if (!this.started) return;
      try {
        const port = this.chrome.runtime.connectNative(NATIVE_HOST);
        this.port = port;
        this.connected = true;
        this.requestInFlight = false;
        port.onMessage.addListener(message => this.onNativeMessage(port, message));
        port.onDisconnect.addListener(() => this.onDisconnect(port));
        this.sendNative("hello");
      } catch (_) {
        this.connected = false;
        this.scheduleReconnect();
      }
    }

    onDisconnect(port) {
      if (port !== this.port) return;
      this.connected = false;
      this.port = null;
      this.requestInFlight = false;
      this.pendingRequestId = null;
      if (this.active) {
        if (this.active.resolveLoad) this.active.resolveLoad("disconnected");
        this.active.resolveTerminal(this.makeResult("Cancelled", false, 0, false));
      }
      this.scheduleReconnect();
    }

    scheduleReconnect() {
      if (!this.started || this.retryTimer != null) return;
      const delays = [5000, 15000, 60000];
      const delay = delays[Math.min(this.retryIndex, delays.length - 1)];
      this.retryIndex += 1;
      this.retryTimer = this.setTimer(() => {
        this.retryTimer = null;
        this.connect();
      }, delay);
    }

    onNativeMessage(port, message) {
      if (port !== this.port || !this.connected || !message || message.ProtocolVersion !== PROTOCOL_VERSION)
        return;
      if (!this.pendingRequestId || message.RequestId !== this.pendingRequestId) return;
      this.requestInFlight = false;
      this.pendingRequestId = null;
      this.retryIndex = 0;
      if (message.Type === "run") {
        if (validTask(message) && !this.active) this.processTask(message);
        else this.requestPoll();
      } else if (message.Type === "ready" || message.Type === "idle" || message.Type === "ack") {
        this.requestPoll();
      } else if (message.Type === "unavailable" || message.Type === "error") {
        this.setTimer(() => this.requestPoll(), 5000);
      }
    }

    requestPoll() {
      return this.sendNative("poll");
    }

    sendNative(type, values) {
      if (!this.connected || !this.port || this.requestInFlight) return false;
      const requestId = `ccm-${Date.now()}-${++this.sequence}`;
      const message = Object.assign({
        Type: type,
        ProtocolVersion: PROTOCOL_VERSION,
        RequestId: requestId,
        Browser: this.browser,
        ExtensionVersion: this.extensionVersion
      }, values || {});
      this.requestInFlight = true;
      this.pendingRequestId = requestId;
      try {
        this.port.postMessage(message);
        return true;
      } catch (_) {
        this.requestInFlight = false;
        this.pendingRequestId = null;
        this.onDisconnect(this.port);
        return false;
      }
    }

    async processTask(nativeTask) {
      const task = {
        taskId: nativeTask.TaskId,
        service: nativeTask.Service,
        challenge: nativeTask.Challenge,
        prompt: nativeTask.Prompt,
        url: nativeTask.Url
      };
      let tab = null;
      let timeoutId = null;
      let expired = false;
      let resolveTerminal;
      const terminal = new Promise(resolve => { resolveTerminal = resolve; });
      const sourcePort = this.port;
      this.active = { task, tabId: null, resolveTerminal, resolveLoad: null };
      try {
        tab = await this.chrome.tabs.create({ url: task.url, active: false });
        this.active.tabId = tab.id;
        if (this.connected && this.port === sourcePort) {
          timeoutId = this.setTimer(() => {
            expired = true;
            if (this.active && this.active.resolveLoad) this.active.resolveLoad("timeout");
            resolveTerminal(this.makeResult("AutomationUnsupported", false, 0, false));
          }, 120000);
        } else {
          resolveTerminal(this.makeResult("Cancelled", false, 0, false));
        }
        const loadState = expired || !this.connected || this.port !== sourcePort ? "cancelled" : await this.waitForLoad(tab);
        if (loadState === "loaded" && !expired && this.connected && this.port === sourcePort) {
          try {
            await this.chrome.tabs.sendMessage(tab.id, { type: "ccm-run", task });
          } catch (_) {
            resolveTerminal(this.makeResult("AutomationUnsupported", false, 0, false));
          }
        }
        const result = await terminal;
        if (this.connected && this.port === sourcePort) this.sendNative("result", {
          TaskId: task.taskId,
          Service: task.service,
          Challenge: task.challenge,
          Outcome: result.outcome,
          MessageSent: result.messageSent,
          ElapsedMilliseconds: Math.round(result.elapsedMilliseconds)
        });
      } catch (_) {
        if (this.connected && this.port === sourcePort) this.sendNative("result", {
          TaskId: task.taskId,
          Service: task.service,
          Challenge: task.challenge,
          Outcome: "AutomationUnsupported",
          MessageSent: false,
          ElapsedMilliseconds: 0
        });
      } finally {
        if (timeoutId != null) this.clearTimer(timeoutId);
        if (this.active && this.active.task.taskId === task.taskId) this.active = null;
        if (tab && typeof tab.id === "number") {
          try { await this.chrome.tabs.remove(tab.id); } catch (_) { }
        }
      }
    }

    async waitForLoad(tab) {
      try {
        const current = await this.chrome.tabs.get(tab.id);
        if (!current) return "removed";
        if (current.status === "complete") return "loaded";
      } catch (_) {
        return "removed";
      }
      return new Promise(resolve => {
        if (!this.active || this.active.tabId !== tab.id) resolve("removed");
        else this.active.resolveLoad = resolve;
      });
    }

    onTabUpdated(tabId, changeInfo) {
      if (this.active && this.active.tabId === tabId && changeInfo && changeInfo.status === "complete" &&
          this.active.resolveLoad) {
        const resolve = this.active.resolveLoad;
        this.active.resolveLoad = null;
        resolve("loaded");
      }
    }

    onTabRemoved(tabId) {
      if (!this.active || this.active.tabId !== tabId) return;
      if (this.active.resolveLoad) {
        const resolveLoad = this.active.resolveLoad;
        this.active.resolveLoad = null;
        resolveLoad("removed");
      }
      this.active.resolveTerminal(this.makeResult("Cancelled", false, 0, false));
    }

    onRuntimeMessage(message, sender) {
      const active = this.active;
      if (!active || !sender || sender.id !== this.chrome.runtime.id || !sender.tab ||
          sender.tab.id !== active.tabId || !validContentResult(message, active)) return false;
      active.resolveTerminal(this.makeResult(message.result.outcome, message.result.messageSent,
        message.result.elapsedMilliseconds, message.result.challengeMatched));
      return false;
    }

    makeResult(outcome, messageSent, elapsedMilliseconds, challengeMatched) {
      return { outcome, messageSent, elapsedMilliseconds, challengeMatched };
    }
  }

  return { BrowserVerificationWorker, NATIVE_HOST, validTask, validContentResult };
});
