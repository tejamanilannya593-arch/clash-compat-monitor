# 浏览器伴侣安装

浏览器伴侣用于在用户明确授权后，对 ChatGPT 和 Gemini 执行一条带随机口令的真实短对话。它只拥有 `nativeMessaging` 权限，以及 `chatgpt.com`、`gemini.google.com` 两个官方站点的访问范围。

1. 先运行发布包根目录的 `Install.cmd`，由安装器注册当前用户的本机消息宿主。
2. Chrome 打开 `chrome://extensions`，Edge 打开 `edge://extensions`，开启“开发者模式”。
3. 选择“加载已解压的扩展”，目录选 `%LOCALAPPDATA%\ClashCompatibilityMonitor\browser-extension`。
4. 保持需要验证的 AI 账号处于登录状态，然后在节点守护中选择“自动实测当前节点”。

扩展创建的测试标签页会自动关闭，但测试对话会保留，不会自动删除。程序不会读取 Cookie、历史记录、账号标识或其他对话内容；只有新回复中的完整随机口令匹配才算通过。

卸载节点守护会删除 Chrome/Edge 当前用户的本机消息注册和本地扩展文件。浏览器中已加载的开发版扩展可在扩展管理页手动移除。
