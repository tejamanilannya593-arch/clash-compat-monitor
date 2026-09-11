function nodeCandidates(config) {
  const flag = /^(?:\uD83C[\uDDE6-\uDDFF]){2}/;
  const rate = /\|\s*([0-5])x\s*$/;
  const seen = new Set();
  return (config.proxies || []).map(proxy => proxy && proxy.name).filter(name => {
    const match = typeof name === 'string' && rate.exec(name);
    if (!match || !flag.test(name) || Number(match[1]) > 3 || seen.has(name)) return false;
    seen.add(name);
    return true;
  });
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
  if (candidates.length < 2) return config;
  const groups = config['proxy-groups'] || (config['proxy-groups'] = []);
  const serviceNames = ['🔍 Google', '🤖 OpenAI', '⌨️ GitHub'];
  const preferred = serviceNames.map(name => groups.find(item => item.name === name))
    .map(group => group && group.now).find(name => candidates.includes(name));
  const ordered = preferred ? [preferred].concat(candidates.filter(name => name !== preferred)) : candidates.slice();

  upsertGroup(groups, { name: '🌐 统一稳定节点', type: 'select', proxies: ordered.slice() });
  upsertGroup(groups, { name: '🧪 兼容性探测', type: 'select', proxies: ordered.slice() });
  serviceNames.forEach(name => {
    const group = groups.find(item => item.name === name);
    if (!group) return;
    group.type = 'select';
    group.proxies = ['🌐 统一稳定节点'];
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
    'DOMAIN-SUFFIX,epicgames.com,🌐 统一稳定节点'
  ];
  config.rules = prependUniqueRules(config.rules, directRules.concat(sharedRules));
  return config;
}

if (typeof module !== 'undefined') module.exports = { main, nodeCandidates };
