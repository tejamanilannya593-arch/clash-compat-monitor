// Clash Verge Rev / Mihomo extension script. No companion process or API listener.
const AUTO = '🌐 自动稳定节点';
const MODE = '🧭 节点模式';
const CHECK_URL = 'https://www.gstatic.com/generate_204';

function main(config) {
  config.ipv6 = false;
  const dns = config.dns || (config.dns = {});
  dns.ipv6 = false;
  const nonNodes = new Set(['direct', 'reject', 'reject-drop', 'pass', 'compatible',
    'select', 'selector', 'url-test', 'fallback', 'load-balance', 'relay']);
  const seen = new Set();
  const nodes = (config.proxies || []).filter(proxy => {
    if (!proxy || typeof proxy.name !== 'string' || !proxy.name.trim() ||
        typeof proxy.type !== 'string' || nonNodes.has(proxy.type.toLowerCase()) ||
        typeof proxy.server !== 'string' || !proxy.server.trim() || seen.has(proxy.name)) return false;
    seen.add(proxy.name);
    return true;
  }).map(proxy => proxy.name);
  const providerMap = config['proxy-providers'] || {};
  const providers = Object.keys(providerMap).filter(name =>
    name.trim() && providerMap[name] && typeof providerMap[name] === 'object');
  if (!nodes.length && !providers.length) return config;

  // Provider health checks are required for provider-only subscriptions.
  for (const name of providers) {
    if (!providerMap[name]['health-check']) {
      providerMap[name]['health-check'] = { enable: true, url: CHECK_URL, interval: 300 };
    }
  }

  const groups = config['proxy-groups'] || (config['proxy-groups'] = []);
  const existing = groups.find(group => group && group.name === MODE);
  const total = nodes.length + (providers.length ? 2 : 0);
  const autoEnabled = total > 1;
  const auto = { name: AUTO, type: 'url-test', url: CHECK_URL,
    interval: 300, timeout: 5000, tolerance: 100, lazy: true };
  if (nodes.length) auto.proxies = nodes.slice();
  if (providers.length) auto.use = providers.slice();
  if (autoEnabled) upsert(groups, auto);
  else remove(groups, AUTO);

  const mode = { name: MODE, type: 'select',
    proxies: (autoEnabled ? [AUTO] : []).concat(nodes) };
  if (providers.length) mode.use = providers.slice();
  if (existing && existing.type === 'select' && Array.isArray(existing.proxies)) {
    const selected = nodes.includes(existing.proxies[0]) ? existing.proxies[0] : null;
    if (selected) mode.proxies = [selected].concat(mode.proxies.filter(name => name !== selected));
  }
  upsert(groups, mode);

  const direct = [
    'DOMAIN-SUFFIX,steamcontent.com,DIRECT',
    'DOMAIN-SUFFIX,steamserver.net,DIRECT',
    'DOMAIN,steampipe.akamaized.net,DIRECT',
    'DOMAIN-SUFFIX,cm.steampowered.com,DIRECT',
    'DOMAIN,stun.chat.bilibili.com,DIRECT',
    'DOMAIN,stun6.chat.bilibili.com,DIRECT'
  ];
  const routed = [
    'DOMAIN-SUFFIX,chatgpt.com,' + MODE,
    'DOMAIN-SUFFIX,openai.com,' + MODE,
    'DOMAIN-SUFFIX,oaistatic.com,' + MODE,
    'DOMAIN-SUFFIX,oaiusercontent.com,' + MODE,
    'DOMAIN,gemini.google.com,' + MODE,
    'DOMAIN,aistudio.google.com,' + MODE,
    'DOMAIN,accounts.google.com,' + MODE,
    'DOMAIN-SUFFIX,github.com,' + MODE,
    'DOMAIN-SUFFIX,githubusercontent.com,' + MODE,
    'DOMAIN-SUFFIX,steamcommunity.com,' + MODE,
    'DOMAIN-SUFFIX,steampowered.com,' + MODE
  ];
  const managed = direct.concat(routed);
  config.rules = managed.concat((config.rules || []).filter(rule => !managed.includes(rule)));
  return config;
}

function upsert(groups, group) {
  const index = groups.findIndex(item => item && item.name === group.name);
  if (index < 0) groups.unshift(group);
  else groups[index] = group;
}

function remove(groups, name) {
  const index = groups.findIndex(item => item && item.name === name);
  if (index >= 0) groups.splice(index, 1);
}

if (typeof module !== 'undefined') module.exports = { main };
