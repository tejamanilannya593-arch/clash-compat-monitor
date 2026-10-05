const noticePattern = '^[^A-Za-z0-9\\u3400-\\u9FFF]*(?:(?:消息|通知|公告|提示|官网|订阅地址|notice|notification|message|subscription info)\\s*[:：]|(?:剩余流量|流量剩余|套餐到期|到期时间|过期时间|下次重置|流量重置)\\s*[:：])';
const noticeName = new RegExp(noticePattern, 'i');
// Mihomo uses Go/RE2: literal Unicode ranges, not JavaScript \\u escapes.
const providerNoticeFilter = '(?i:' + noticePattern.replace('\\u3400', '㐀').replace('\\u9FFF', '鿿') + ')';

function isNotice(name) {
  return typeof name === 'string' && noticeName.test(name);
}

function removeNotices(config) {
  if (Array.isArray(config.proxies)) config.proxies = config.proxies.filter(proxy => proxy && !isNotice(proxy.name));
  for (const group of config['proxy-groups'] || []) {
    if (Array.isArray(group.proxies)) {
      const before = group.proxies.length;
      group.proxies = group.proxies.filter(name => !isNotice(name));
      if (before && !group.proxies.length && !(group.use || []).length && !group['include-all'] && !group['include-all-proxies'] && !group['include-all-providers'])
        group.proxies = ['REJECT'];
    }
    if (isNotice(group.now)) delete group.now;
    if ((group.use || []).length || group['include-all'] || group['include-all-proxies'] || group['include-all-providers']) {
      const previous = group['exclude-filter'] || '';
      if (!previous.includes(providerNoticeFilter))
        group['exclude-filter'] = previous ? '(' + previous + ')|' + providerNoticeFilter : providerNoticeFilter;
    }
  }
}

function nodeCandidates(config) {
  const nonLeafTypes = new Set([
    'direct', 'reject', 'reject-drop', 'pass', 'compatible',
    'select', 'selector', 'url-test', 'fallback', 'load-balance', 'relay'
  ]);
  const seen = new Set();
  return (config.proxies || []).filter(proxy => {
    if (!proxy || typeof proxy.name !== 'string' || !proxy.name.trim()) return false;
    if (isNotice(proxy.name)) return false;
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
  if (providers.length) {
    group.use = providers.slice();
    group['exclude-filter'] = providerNoticeFilter;
  }
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

const legacyShared = '🌐 统一稳定节点';
const probeName = '🧪 兼容性探测';

function ruleTargetIndex(parts) {
  return parts[parts.length - 1] === 'no-resolve' ? parts.length - 2 : parts.length - 1;
}

function existingSelector(groups, rules) {
  const eligible = groups.filter(group => group.type === 'select' &&
    group.name !== legacyShared && group.name !== probeName && group.hidden !== true);
  for (const name of ['🚀 节点选择', 'PROXY', 'Proxy', '代理']) {
    const found = eligible.find(group => group.name === name);
    if (found) return found;
  }
  for (const rule of rules || []) {
    const parts = rule.split(',');
    if (parts[0] === 'MATCH' || parts[0] === 'FINAL') {
      const found = eligible.find(group => group.name === parts[1]);
      if (found) return found;
    }
  }
  return eligible[0];
}

function migrateLegacy(config, groups, selected, candidates, providers) {
  const old = groups.find(group => group.name === legacyShared);
  const leaves = new Set();
  const uses = new Set();
  const visited = new Set();
  function collect(name) {
    if (visited.has(name)) return;
    visited.add(name);
    if (candidates.includes(name)) { leaves.add(name); return; }
    const group = groups.find(item => item.name === name);
    if (!group) return;
    for (const provider of group.use || []) if (providers.includes(provider)) uses.add(provider);
    if (group['include-all'] || group['include-all-proxies']) candidates.forEach(name => leaves.add(name));
    if (group['include-all'] || group['include-all-providers']) providers.forEach(name => uses.add(name));
    (group.proxies || []).forEach(collect);
  }
  if (old) collect(legacyShared);
  if (!leaves.size && !uses.size) {
    candidates.forEach(name => leaves.add(name));
    providers.forEach(name => uses.add(name));
  }
  // Any descendant of the selected group must expand the old leaves instead of
  // pointing back to the selected group and creating an indirect cycle.
  const descendants = new Set();
  function visit(name) {
    if (descendants.has(name) || name === legacyShared) return;
    descendants.add(name);
    const group = groups.find(item => item.name === name);
    if (group) (group.proxies || []).forEach(visit);
  }
  if (selected) visit(selected.name);
  for (const group of groups) {
    if (group.name === legacyShared) continue;
    const expand = !selected || descendants.has(group.name);
    if ((group.proxies || []).includes(legacyShared)) {
      group.proxies = [...new Set(group.proxies.flatMap(name =>
        name !== legacyShared ? [name] : expand ? [...leaves] : [selected.name]))];
      if (expand && uses.size) group.use = [...new Set((group.use || []).concat([...uses]))];
      if (!group.proxies.length && !(group.use || []).length) group.proxies = ['DIRECT'];
    }
    if (group.now === legacyShared) {
      if (expand && leaves.size) group.now = [...leaves][0];
      else if (!expand) group.now = selected.name;
      else delete group.now;
    }
  }
  const target = selected ? selected.name : [...leaves][0] || 'DIRECT';
  config.rules = (config.rules || []).filter(rule => rule !== `DOMAIN-SUFFIX,18comic.vip,${legacyShared}`)
    .map(rule => {
      const parts = rule.split(',');
      const index = ruleTargetIndex(parts);
      if (parts[index] === legacyShared) parts[index] = target;
      return parts.join(',');
    });
  for (const listener of config.listeners || []) if (listener.proxy === legacyShared) listener.proxy = target;
  config['proxy-groups'] = groups.filter(group => group.name !== legacyShared);
}

function addMissingRules(rules, additions) {
  // Existing explicit/direct rules retain precedence; append additions just
  // before the terminal catch-all, rather than rewriting the user's routing.
  const key = rule => {
    const parts = rule.split(',');
    return parts.slice(0, ruleTargetIndex(parts)).join(',');
  };
  const existing = new Set(rules.map(key));
  const missing = additions.filter(rule => !existing.has(key(rule)));
  const terminal = rules.findIndex(rule => /^(MATCH|FINAL),/.test(rule));
  const index = terminal < 0 ? rules.length : terminal;
  return rules.slice(0, index).concat(missing, rules.slice(index));
}

function main(config, profileName) {
  removeNotices(config);
  config.profile = config.profile || {};
  config.profile['store-selected'] = true;
  const candidates = nodeCandidates(config);
  const providers = providerNames(config);
  let groups = config['proxy-groups'] || (config['proxy-groups'] = []);
  const selected = existingSelector(groups, config.rules);
  migrateLegacy(config, groups, selected, candidates, providers);
  groups = config['proxy-groups'];
  removeNotices(config);
  if (!candidates.length && !providers.length) return config;
  const preferred = selected && [selected.now].concat(selected.proxies || []).find(name => candidates.includes(name));
  const ordered = preferred ? [preferred].concat(candidates.filter(name => name !== preferred)) : candidates.slice();

  upsertGroup(groups, { ...managedGroup(probeName, ordered, providers), hidden: true });

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
  ].map(rule => rule.replace(legacyShared, selected ? selected.name : 'DIRECT'));
  if (selected) config.rules = addMissingRules(config.rules, directRules.concat(sharedRules));
  return config;
}

if (typeof module !== 'undefined') module.exports = { main, nodeCandidates, providerNames, isNotice };
