function nodeCandidates(config) {
  const nonLeafTypes = new Set([
    'direct', 'reject', 'reject-drop', 'pass', 'compatible',
    'select', 'selector', 'url-test', 'fallback', 'load-balance', 'relay'
  ]);
  const seen = new Set();
  return (config.proxies || []).filter(proxy => {
    if (!proxy || typeof proxy.name !== 'string' || !proxy.name.trim()) return false;
    if (typeof proxy.type !== 'string' || nonLeafTypes.has(proxy.type.toLowerCase())) return false;
    if (typeof proxy.server !== 'string' || !proxy.server.trim()) return false;
    if (seen.has(proxy.name)) return false;
    seen.add(proxy.name);
    return true;
  }).map(proxy => proxy.name);
}

function providerNames(config) {
  const providers = config['proxy-providers'] || {};
  return Object.keys(providers).filter(name =>
    name.trim() && providers[name] && typeof providers[name] === 'object'
  );
}

function managedGroup(name, proxies, providers) {
  const group = { name, type: 'select' };
  if (proxies.length) group.proxies = proxies.slice();
  if (providers.length) group.use = providers.slice();
  return group;
}

function upsertGroup(groups, group) {
  const index = groups.findIndex(item => item.name === group.name);
  if (index >= 0) groups[index] = group;
  else groups.unshift(group);
}

function upsertListener(listeners, listener) {
  const index = listeners.findIndex(item => item.name === listener.name);
  if (index >= 0) listeners[index] = listener;
  else listeners.push(listener);
}

function prependUniqueRules(rules, additions) {
  const managed = new Set(additions);
  return additions.concat((rules || []).filter(rule => !managed.has(rule)));
}

function main(config, profileName) {
  // The current machine has no usable native IPv6 route. Letting Mihomo return
  // fake IPv6 addresses makes direct applications wait for IPv6 timeouts before
  // falling back to IPv4, which is especially visible in WeChat media loading.
  config.ipv6 = false;
  const dns = config.dns || (config.dns = {});
  dns.ipv6 = false;

  const candidates = nodeCandidates(config);
  const providers = providerNames(config);
  if (!candidates.length && !providers.length) return config;
  const groups = config['proxy-groups'] || (config['proxy-groups'] = []);
  const serviceNames = ['🔍 Google', '🤖 OpenAI', '⌨️ GitHub'];
  const preferred = serviceNames.map(name => groups.find(item => item.name === name))
    .map(group => group && group.now).find(name => candidates.includes(name));
  const ordered = preferred ? [preferred].concat(candidates.filter(name => name !== preferred)) : candidates.slice();

  upsertGroup(groups, managedGroup('🌐 统一稳定节点', ordered, providers));
  upsertGroup(groups, managedGroup('🧪 兼容性探测', ordered, providers));
  serviceNames.forEach(name => {
    const group = groups.find(item => item.name === name);
    if (!group) return;
    group.type = 'select';
    group.proxies = ['🌐 统一稳定节点'];
    delete group.use;
    delete group.url; delete group.interval; delete group.tolerance; delete group.lazy;
  });

  const listeners = config.listeners || (config.listeners = []);
  upsertListener(listeners, {
    name: 'compatibility-probe', type: 'http', listen: '127.0.0.1', port: 7896, proxy: '🧪 兼容性探测'
  });

  const directRules = [
    'DOMAIN-SUFFIX,steamcontent.com,DIRECT',
    'DOMAIN-SUFFIX,steamserver.net,DIRECT',
    'DOMAIN,steampipe.akamaized.net,DIRECT',
    'DOMAIN-SUFFIX,cm.steampowered.com,DIRECT',
    'DOMAIN,stun.chat.bilibili.com,DIRECT',
    'DOMAIN,stun6.chat.bilibili.com,DIRECT'
  ];
  const sharedRules = [
    'DOMAIN,gemini.google.com,🌐 统一稳定节点',
    'DOMAIN,aistudio.google.com,🌐 统一稳定节点',
    'DOMAIN,generativelanguage.googleapis.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,chatgpt.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,openai.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,oaistatic.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,oaiusercontent.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,github.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,githubusercontent.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,steampowered.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,steamcommunity.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,steam-chat.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,discord.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,discordapp.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,spotify.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,scdn.co,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,epicgames.com,🌐 统一稳定节点',
    'DOMAIN-SUFFIX,18comic.vip,🌐 统一稳定节点'
  ];
  config.rules = prependUniqueRules(config.rules, directRules.concat(sharedRules));
  return config;
}

if (typeof module !== 'undefined') module.exports = { main, nodeCandidates, providerNames };
