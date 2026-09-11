(function (root, factory) {
  "use strict";
  const api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  root.CCMAdapterCore = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  "use strict";

  const DEFAULT_WAIT_MS = 90000;
  const DEFAULT_POLL_MS = 500;

  function findFirst(page, selectors) {
    for (const selector of selectors || []) {
      const element = page.querySelector(selector);
      if (element) return element;
    }
    return null;
  }

  function findAll(page, selectors) {
    const found = [];
    const seen = new Set();
    for (const selector of selectors || []) {
      for (const element of Array.from(page.querySelectorAll(selector) || [])) {
        if (!seen.has(element)) {
          seen.add(element);
          found.push(element);
        }
      }
    }
    return found;
  }

  function normalizeWhitespace(value) {
    return String(value == null ? "" : value).replace(/\s+/g, " ").trim();
  }

  function escapeRegExp(value) {
    return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  }

  function containsChallenge(text, challenge) {
    const normalized = normalizeWhitespace(text);
    const exactToken = new RegExp(`(^|[^A-Za-z0-9-])${escapeRegExp(challenge)}(?=$|[^A-Za-z0-9-])`);
    return exactToken.test(normalized);
  }

  function result(outcome, messageSent, elapsedMilliseconds, challengeMatched) {
    return {
      outcome,
      messageSent,
      elapsedMilliseconds: Math.max(0, Math.round(elapsedMilliseconds || 0)),
      challengeMatched
    };
  }

  function createEvent(element, type, init) {
    const view = element.ownerDocument && element.ownerDocument.defaultView;
    const Constructor = view && (type === "input" ? view.InputEvent : view.Event);
    if (Constructor) {
      try { return new Constructor(type, init); } catch (_) { }
    }
    return { type };
  }

  function setComposerText(composer, prompt) {
    composer.focus();
    if ("value" in composer) {
      const prototype = Object.getPrototypeOf(composer);
      const descriptor = prototype && Object.getOwnPropertyDescriptor(prototype, "value");
      if (descriptor && descriptor.set) descriptor.set.call(composer, prompt);
      else composer.value = prompt;
    } else {
      composer.textContent = prompt;
    }
    composer.dispatchEvent(createEvent(composer, "input", {
      bubbles: true, inputType: "insertText", data: prompt
    }));
    composer.dispatchEvent(createEvent(composer, "change", { bubbles: true }));
  }

  function validTask(task) {
    return task && typeof task.challenge === "string" && /^CCM-[A-Za-z0-9-]{1,76}$/.test(task.challenge) &&
      typeof task.prompt === "string" && task.prompt.length > 0 && task.prompt.length <= 300;
  }

  async function runConversation(page, task, profile, options) {
    options = options || {};
    const now = options.now || Date.now;
    const wait = options.wait || (milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds)));
    const signal = options.signal || { aborted: false };
    const started = now();
    const elapsed = () => now() - started;

    if (!page || !validTask(task) || !profile) return result("AutomationUnsupported", false, elapsed(), false);
    if (signal.aborted) return result("Cancelled", false, elapsed(), false);
    if (findFirst(page, profile.signIn)) return result("SignInRequired", false, elapsed(), false);
    if (findFirst(page, profile.challenge)) return result("ChallengeRequired", false, elapsed(), false);
    if (findFirst(page, profile.errors)) return result("ConversationError", false, elapsed(), false);

    const composer = findFirst(page, profile.composer);
    const send = findFirst(page, profile.send);
    if (!composer || !send || send.disabled) return result("AutomationUnsupported", false, elapsed(), false);

    const existingReplies = new Set(findAll(page, profile.replies));
    setComposerText(composer, task.prompt);
    if (signal.aborted) return result("Cancelled", false, elapsed(), false);
    send.click();
    const messageSent = true;
    const maximum = options.maxWaitMilliseconds == null ? DEFAULT_WAIT_MS : options.maxWaitMilliseconds;
    const interval = options.pollIntervalMilliseconds == null ? DEFAULT_POLL_MS : options.pollIntervalMilliseconds;

    while (elapsed() < maximum) {
      if (signal.aborted) return result("Cancelled", messageSent, elapsed(), false);
      if (findFirst(page, profile.signIn)) return result("SignInRequired", messageSent, elapsed(), false);
      if (findFirst(page, profile.challenge)) return result("ChallengeRequired", messageSent, elapsed(), false);
      if (findFirst(page, profile.errors)) return result("ConversationError", messageSent, elapsed(), false);
      for (const reply of findAll(page, profile.replies)) {
        if (!existingReplies.has(reply) && containsChallenge(reply.textContent, task.challenge))
          return result("Passed", messageSent, elapsed(), true);
      }
      await wait(interval);
    }
    return result("GenerationTimeout", messageSent, elapsed(), false);
  }

  return { runConversation, normalizeWhitespace, containsChallenge };
});
