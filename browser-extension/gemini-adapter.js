(function (root, factory) {
  "use strict";
  const core = typeof module === "object" && module.exports ? require("./adapter-core") : root.CCMAdapterCore;
  const api = factory(core);
  if (typeof module === "object" && module.exports) module.exports = api;
  root.CCMGeminiAdapter = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function (core) {
  "use strict";

  const GEMINI_PROFILE = {
    composer: [
      "rich-textarea [contenteditable='true']",
      ".ql-editor[contenteditable='true']",
      "textarea[aria-label*='prompt']"
    ],
    send: [
      "button[aria-label='Send message']",
      "button[aria-label*='Send message']",
      "button.send-button"
    ],
    replies: [
      ".model-response-text",
      "message-content",
      ".response-container-content"
    ],
    userMessages: [
      "user-query",
      "[data-test-id='user-query']",
      ".user-query"
    ],
    signIn: [
      "a[href*='accounts.google.com']",
      "button[data-test-id='sign-in-button']"
    ],
    challenge: [
      "iframe[src*='/recaptcha/']",
      "iframe[src*='challenges.cloudflare.com']",
      "[data-test-id='challenge']"
    ],
    errors: [
      "[data-test-id='response-error']",
      ".error-message",
      "div[role='alert']"
    ]
  };

  function runGeminiAdapter(page, task, options) {
    return core.runConversation(page, task, GEMINI_PROFILE, options);
  }

  return { runGeminiAdapter, GEMINI_PROFILE };
});
