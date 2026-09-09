const assert = require('assert');
const { main, nodeCandidates } = require('./enhancement');

const fixture = {
  ipv6: true,
  dns: { ipv6: true, 'enhanced-mode': 'fake-ip' },
  proxies: [
    { name: 'DIRECT', type: 'direct' },
    { name: '消息: 17条未读，在APP查看' },
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
for (const name of ['🔍 Google', '🤖 OpenAI', '⌨️ GitHub']) {
  assert.deepStrictEqual(once['proxy-groups'].find(x => x.name === name).proxies, ['🌐 统一稳定节点']);
}
assert.deepStrictEqual(once.listeners.find(x => x.name === 'compatibility-probe'), {
  name: 'compatibility-probe', type: 'http', listen: '127.0.0.1', port: 7896, proxy: '🧪 兼容性探测'
});
assert.strictEqual(once.rules[0], 'DOMAIN-SUFFIX,steamcontent.com,DIRECT');
assert(once.rules.indexOf('DOMAIN-SUFFIX,steamcontent.com,DIRECT') < once.rules.indexOf('DOMAIN-SUFFIX,steampowered.com,🌐 统一稳定节点'));
assert(once.rules.includes('DOMAIN,gemini.google.com,🌐 统一稳定节点'));
assert(once.rules.includes('DOMAIN-SUFFIX,chatgpt.com,🌐 统一稳定节点'));
assert(once.rules.includes('DOMAIN-SUFFIX,github.com,🌐 统一稳定节点'));
assert(once.rules.includes('DOMAIN,stun.chat.bilibili.com,DIRECT'));
const twice = main(JSON.parse(JSON.stringify(once)));
assert.deepStrictEqual(twice, once);
console.log('PASS enhancement filtering, routing, listener, and idempotency');
