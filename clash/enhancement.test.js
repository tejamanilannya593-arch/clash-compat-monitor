const assert = require('assert');
const { main, nodeCandidates, providerNames } = require('./enhancement');

const fixture = {
  ipv6: true,
  dns: { ipv6: true, 'enhanced-mode': 'fake-ip' },
  proxies: [
    { name: 'DIRECT', type: 'direct' },
    { name: '消息: 17条未读，在APP查看', type: 'trojan', server: 'notice.example', port: 36113 },
    { name: '剩余流量：120 GB', type: 'ss', server: 'notice.example', port: 443 },
    { name: 'Tokyo-A', type: 'ss', server: 'one.example', port: 443 },
    { name: '普通节点 无倍率', type: 'vless', server: 'two.example', port: 443 },
    { name: '🇭🇰 香港 I1 | IEPL | 3x', type: 'trojan', server: 'three.example', port: 443 },
    { name: '🇯🇵 日本 V1 | IPv6 | 3x', type: 'hysteria2', server: 'four.example', port: 443 },
    { name: '🇺🇸 美国 I0 | ChatGPT | 1x', type: 'vmess', server: 'five.example', port: 443 },
    { name: '🇹🇼 台湾 T1 | IPv6 | 1x', type: 'tuic', server: 'six.example', port: 443 },
    { name: '高倍率仍需实测 | 5x', type: 'ss', server: 'seven.example', port: 443 },
    { name: 'Tokyo-A', type: 'ss', server: 'duplicate.example', port: 443 },
    { name: '不是叶节点', type: 'select' }
  ],
  'proxy-groups': [
    { name: '🚀 节点选择', type: 'select', now: '消息: 17条未读，在APP查看', proxies: ['消息: 17条未读，在APP查看', 'Tokyo-A'] },
    { name: '其他选择', type: 'select', proxies: ['消息: 17条未读，在APP查看', 'Tokyo-A', 'DIRECT'] },
    { name: '🔍 Google', type: 'url-test', now: '🇯🇵 日本 V1 | IPv6 | 3x', proxies: ['🇯🇵 日本 V1 | IPv6 | 3x'] },
    { name: '🤖 OpenAI', type: 'select', proxies: ['🇺🇸 美国 I0 | ChatGPT | 1x'] },
    { name: '⌨️ GitHub', type: 'select', proxies: ['🇸🇬 新加坡 M2 | BHE | 3x'] }
  ],
  rules: ['DOMAIN-SUFFIX,steampowered.com,旧策略', 'MATCH,DIRECT']
};

assert.deepStrictEqual(nodeCandidates(fixture), [
  'Tokyo-A', '普通节点 无倍率', '🇭🇰 香港 I1 | IEPL | 3x', '🇯🇵 日本 V1 | IPv6 | 3x',
  '🇺🇸 美国 I0 | ChatGPT | 1x', '🇹🇼 台湾 T1 | IPv6 | 1x', '高倍率仍需实测 | 5x'
]);
const once = main(JSON.parse(JSON.stringify(fixture)));
assert.strictEqual(once.ipv6, false);
assert.strictEqual(once.dns.ipv6, false);
const shared = once['proxy-groups'].find(x => x.name === '🌐 统一稳定节点');
const probe = once['proxy-groups'].find(x => x.name === '🧪 兼容性探测');
assert(shared && probe);
assert.strictEqual(shared.proxies[0], '🇯🇵 日本 V1 | IPv6 | 3x');
assert.deepStrictEqual(shared.proxies, probe.proxies);
for (const name of ['🔍 Google', '🤖 OpenAI', '⌨️ GitHub', '🚀 节点选择']) {
  assert.deepStrictEqual(once['proxy-groups'].find(x => x.name === name).proxies, ['🌐 统一稳定节点']);
}
assert(!once.proxies.some(x => x.name.startsWith('消息:') || x.name.startsWith('剩余流量')));
assert.deepStrictEqual(once['proxy-groups'].find(x => x.name === '其他选择').proxies, ['Tokyo-A', 'DIRECT']);
assert.strictEqual(once.profile['store-selected'], true);
assert(!once['proxy-groups'].find(x => x.name === '🚀 节点选择').now);
assert.deepStrictEqual(once.listeners.find(x => x.name === 'compatibility-probe'), {
  name: 'compatibility-probe', type: 'http', listen: '127.0.0.1', port: 7896, proxy: '🧪 兼容性探测'
});
assert.strictEqual(once.rules[0], 'DOMAIN-SUFFIX,steamcontent.com,DIRECT');
assert(once.rules.indexOf('DOMAIN-SUFFIX,steamcontent.com,DIRECT') < once.rules.indexOf('DOMAIN-SUFFIX,steampowered.com,🌐 统一稳定节点'));
assert(once.rules.includes('DOMAIN,gemini.google.com,🌐 统一稳定节点'));
assert(once.rules.includes('DOMAIN-SUFFIX,chatgpt.com,🌐 统一稳定节点'));
assert(once.rules.includes('DOMAIN-SUFFIX,github.com,🌐 统一稳定节点'));
assert(once.rules.includes('DOMAIN-SUFFIX,18comic.vip,🌐 统一稳定节点'));
assert(once.rules.includes('DOMAIN,stun.chat.bilibili.com,DIRECT'));
const twice = main(JSON.parse(JSON.stringify(once)));
assert.deepStrictEqual(twice, once);

const providerOnly = {
  ipv6: true,
  dns: { ipv6: true },
  'proxy-providers': {
    airportA: { type: 'http', url: 'https://provider.invalid/a' },
    localNodes: { type: 'file', path: './nodes.yaml' },
    broken: null
  },
  rules: []
};
assert.deepStrictEqual(providerNames(providerOnly), ['airportA', 'localNodes']);
const providerResult = main(JSON.parse(JSON.stringify(providerOnly)));
const providerShared = providerResult['proxy-groups'].find(x => x.name === '🌐 统一稳定节点');
const providerProbe = providerResult['proxy-groups'].find(x => x.name === '🧪 兼容性探测');
assert.deepStrictEqual(providerShared.use, ['airportA', 'localNodes']);
assert.deepStrictEqual(providerProbe.use, ['airportA', 'localNodes']);
assert.strictEqual(Object.prototype.hasOwnProperty.call(providerShared, 'proxies'), false);
assert(providerShared['exclude-filter'].includes('消息'));
assert(providerProbe['exclude-filter'].includes('消息'));
assert(!providerProbe['exclude-filter'].includes('\\u3400'));
assert.deepStrictEqual(providerResult['proxy-groups'].find(x => x.name === '🚀 节点选择').proxies, ['🌐 统一稳定节点']);

const mixed = JSON.parse(JSON.stringify(fixture));
mixed['proxy-providers'] = { airportA: { type: 'http', url: 'https://provider.invalid/a' } };
const mixedResult = main(mixed);
assert.deepStrictEqual(mixedResult['proxy-groups'].find(x => x.name === '🌐 统一稳定节点').use, ['airportA']);
assert(mixedResult['proxy-groups'].find(x => x.name === '🌐 统一稳定节点').proxies.includes('Tokyo-A'));

const single = { ipv6: true, dns: { ipv6: true }, proxies: [{ name: 'only-node', type: 'ss', server: 'one.invalid', port: 443 }], rules: [] };
const singleResult = main(single);
assert.deepStrictEqual(singleResult['proxy-groups'].find(x => x.name === '🌐 统一稳定节点').proxies, ['only-node']);
assert.deepStrictEqual(main(JSON.parse(JSON.stringify(providerResult))), providerResult);
const metadataOnly = main({ proxies: [{ name: '消息: 1条未读', type: 'trojan', server: 'notice.invalid' }],
  'proxy-groups': [{ name: 'old', type: 'select', proxies: ['消息: 1条未读'] }] });
assert.deepStrictEqual(metadataOnly['proxy-groups'][0].proxies, ['REJECT']);
const included = main({ 'proxy-providers': { a: { type: 'file', path: './a.yaml' } },
  'proxy-groups': [{ name: 'other', type: 'select', 'include-all-providers': true, 'exclude-filter': 'old-filter' }] });
assert(included['proxy-groups'].find(x => x.name === 'other')['exclude-filter'].includes('old-filter'));
assert(included['proxy-groups'].find(x => x.name === 'other')['exclude-filter'].includes('消息'));
assert.deepStrictEqual(main(JSON.parse(JSON.stringify(included))), included);
console.log('PASS enhancement filtering, routing, listener, and idempotency');
