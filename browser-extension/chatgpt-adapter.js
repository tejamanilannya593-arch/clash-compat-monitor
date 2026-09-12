(function (root, factory) {
  "use strict";
  const core = typeof module === "object" && module.exports ? require("./adapter-core") : root.CCMAdapterCore;
  const api = factory(core);
  if (typeof module === "object" && module.exports) module.exports = api;
  root.CCMChatGptAdapter = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function (core) {
  "use strict";

  const CHATGPT_PROFILE = {
    composer: [
      "#prompt-textarea",
      "textarea[data-id='root']",
      "div[contenteditable='true'][data-testid='composer-text-input']"
    ],
    send: [
      "button[data-testid='send-button']",
      "button[aria-label='Send prompt']",
      "button[aria-label='Send message']"
    ],
    replies: [
      "[data-message-author-role='assistant']",
      "article [data-message-author-role='assistant']"
    ],
    userMessages: ["[data-message-author-role='user']"],
    signIn: [
      "button[data-testid='login-button']",
      "a[href*='/auth/login']",
      "a[href*='/auth0/']"
    ],
    challenge: [
      "iframe[src*='challenges.cloudflare.com']",
      "#challenge-running",
      "[data-testid='challenge-form']"
    ],
    errors: [
      "[data-testid='conversation-turn-error']",
      "[data-testid='error-message']",
      "div[role='alert']"
    ]
  };

  function runChatGptAdapter(page, task, options) {
    return core.runConversation(page, task, CHATGPT_PROFILE, options);
  }

  return { runChatGptAdapter, CHATGPT_PROFILE };
});
