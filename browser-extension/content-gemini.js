(function (root, factory) {
  "use strict";
  const adapter = typeof module === "object" && module.exports ? require("./gemini-adapter") : root.CCMGeminiAdapter;
  const api = factory(adapter);
  if (typeof module === "object" && module.exports) module.exports = api;
  else {
    root.CCMGeminiContent = api;
    api.install(root.chrome, root.document, root.location);
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function (adapterApi) {
  "use strict";

  function validTask(task) {
    return task && /^[0-9a-f]{32}$/.test(task.taskId || "") && task.service === "Gemini" &&
      /^CCM-[A-Za-z0-9-]{1,76}$/.test(task.challenge || "") &&
      typeof task.prompt === "string" && task.prompt.length > 0 && task.prompt.length <= 300;
  }

  function createGeminiContentHandler(dependencies) {
    const runtime = dependencies.runtime;
    const page = dependencies.page;
    const location = dependencies.location;
    const adapter = dependencies.adapter;
    return function (message, sender) {
      if (!message || message.type !== "ccm-run" || !sender || sender.id !== runtime.id ||
          !validTask(message.task) || !location || location.origin !== "https://gemini.google.com") return false;
      Promise.resolve(adapter(page, message.task)).then(result => runtime.sendMessage({
        type: "ccm-result",
        taskId: message.task.taskId,
        service: message.task.service,
        challenge: message.task.challenge,
        result
      })).catch(() => { });
      return false;
    };
  }

  function install(chromeApi, page, location) {
    chromeApi.runtime.onMessage.addListener(createGeminiContentHandler({
      runtime: chromeApi.runtime,
      page,
      location,
      adapter: adapterApi.runGeminiAdapter
    }));
  }

  return { createGeminiContentHandler, install, validTask };
});
