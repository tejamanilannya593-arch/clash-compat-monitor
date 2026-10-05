const assert = require('assert');
const { main } = require('../clash/enhancement');
const shared = '🌐 统一稳定节点';
const probeName = '🧪 兼容性探测';
const node = name => ({ name, type: 'ss', server: `${name}.invalid`, port: 443 });
const copy = value => JSON.parse(JSON.stringify(value));
const group = (value, name) => value['proxy-groups'].find(x => x.name === name);
const apply = value => {
  const result = main(copy(value));
  assert(!group(result, shared), 'dedicated shared group must not be created or retained');
  assert.deepStrictEqual(main(copy(result)), result, 'enhancement must be idempotent');
  return result;
};
const base = {
  proxies: [node('A'), node('B')],
  'proxy-groups': [{ name: '🚀 节点选择', type: 'select', proxies: ['A', 'B'], now: 'B' }],
  rules: ['DOMAIN-SUFFIX,chatgpt.com,DIRECT', 'MATCH,🚀 节点选择']
};
const ordinary = apply(base);
assert.strictEqual(Object.prototype.hasOwnProperty.call(ordinary, 'ipv6'), false);
assert.strictEqual(Object.prototype.hasOwnProperty.call(ordinary, 'dns'), false);
for (const enabled of [true, false]) {
  const input = { ...base, ipv6: enabled, dns: { ipv6: enabled, nameserver: ['192.0.2.1'], 'enhanced-mode': 'fake-ip' } };
  const result = apply(input);
  assert.strictEqual(result.ipv6, input.ipv6);
  assert.deepStrictEqual(result.dns, input.dns);
}
assert.deepStrictEqual(group(ordinary, '🚀 节点选择'), base['proxy-groups'][0]);
assert.strictEqual(group(ordinary, probeName).hidden, true);
assert.deepStrictEqual(group(ordinary, probeName).proxies, ['B', 'A']);
assert.strictEqual(ordinary.listeners.find(x => x.name === 'compatibility-probe').port, 7896);
assert(ordinary.rules.includes('DOMAIN-SUFFIX,chatgpt.com,DIRECT'));
assert(!ordinary.rules.includes('DOMAIN-SUFFIX,chatgpt.com,🚀 节点选择'));
assert(ordinary.rules.includes('DOMAIN,gemini.google.com,🚀 节点选择'));
assert(!ordinary.rules.some(x => x.includes('18comic')));

for (const [names, rules, expected] of [
  [['Other', 'PROXY', '🚀 节点选择'], ['MATCH,Other'], '🚀 节点选择'],
  [['Other', 'PROXY'], ['MATCH,Other'], 'PROXY'],
  [['Other', 'Proxy'], ['MATCH,Other'], 'Proxy'],
  [['Other', '代理'], ['MATCH,Other'], '代理'],
  [['First', 'Chosen'], ['FINAL,Chosen'], 'Chosen'],
  [['First', 'Chosen'], ['MATCH,Chosen'], 'Chosen'],
  [['First', 'Second'], ['MATCH,DIRECT'], 'First']
]) {
  const result = apply({ proxies: [node('A')], 'proxy-groups': names.map(name => ({ name, type: 'select', proxies: ['A'] })), rules });
  assert(result.rules.includes(`DOMAIN,gemini.google.com,${expected}`), `select ${expected}`);
}

const providers = { airport: { type: 'file', path: './airport.yaml' } };
const providerResult = apply({
  'proxy-providers': providers,
  'proxy-groups': [{ name: 'PROXY', type: 'select', use: ['airport'], filter: 'US', icon: 'custom', 'exclude-type': 'http' }],
  rules: ['MATCH,PROXY']
});
assert.deepStrictEqual(group(providerResult, probeName).use, ['airport']);
assert.strictEqual(group(providerResult, 'PROXY').filter, 'US');
assert.strictEqual(group(providerResult, 'PROXY').icon, 'custom');
assert.strictEqual(group(providerResult, 'PROXY')['exclude-type'], 'http');
assert.deepStrictEqual(group(providerResult, 'PROXY').use, ['airport']);

const special = { name: '🔍 Google', type: 'url-test', use: ['airport'], proxies: ['A'], filter: 'Tokyo', 'exclude-filter': 'slow', url: 'https://example.invalid/', interval: 77, tolerance: 19, lazy: true, hidden: false };
const preserved = apply({ ...base, 'proxy-providers': providers, 'proxy-groups': [...base['proxy-groups'], copy(special)] });
for (const key of Object.keys(special).filter(x => x !== 'exclude-filter')) assert.deepStrictEqual(group(preserved, special.name)[key], special[key]);
assert(group(preserved, special.name)['exclude-filter'].includes('slow'));

const migrated = apply({
  proxies: [node('A'), node('B')], 'proxy-providers': providers,
  'proxy-groups': [
    { name: shared, type: 'select', proxies: ['B', 'A'], use: ['airport'] },
    { name: '🚀 节点选择', type: 'select', proxies: [shared], now: shared, filter: 'keep' },
    { name: '🤖 OpenAI', type: 'fallback', proxies: [shared], interval: 12 }
  ],
  listeners: [{ name: 'old-service', type: 'http', port: 7898, proxy: shared }],
  rules: [`DOMAIN-SUFFIX,18comic.vip,${shared}`, `DOMAIN-SUFFIX,example.com,${shared},no-resolve`, `MATCH,${shared}`]
});
assert.deepStrictEqual(group(migrated, '🚀 节点选择').proxies, ['B', 'A']);
assert.deepStrictEqual(group(migrated, '🚀 节点选择').use, ['airport']);
assert.strictEqual(group(migrated, '🚀 节点选择').filter, 'keep');
assert.strictEqual(group(migrated, '🚀 节点选择').now, 'B');
assert.deepStrictEqual(group(migrated, '🤖 OpenAI').proxies, ['🚀 节点选择']);
assert.strictEqual(group(migrated, '🤖 OpenAI').type, 'fallback');
assert.strictEqual(migrated.listeners.find(x => x.name === 'old-service').proxy, '🚀 节点选择');
assert(migrated.rules.includes('DOMAIN-SUFFIX,example.com,🚀 节点选择,no-resolve'));
assert(!JSON.stringify(migrated).includes(shared));

const indirect = apply({ proxies: [node('A')], 'proxy-groups': [
  { name: shared, type: 'select', proxies: ['A'] },
  { name: 'PROXY', type: 'select', proxies: ['Service'] },
  { name: 'Service', type: 'select', proxies: [shared] }
], rules: [`MATCH,${shared}`] });
assert.deepStrictEqual(group(indirect, 'Service').proxies, ['A'], 'migration must not form PROXY -> Service -> PROXY');

const providerMigration = apply({ 'proxy-providers': providers, 'proxy-groups': [
  { name: shared, type: 'select', use: ['airport'] },
  { name: 'PROXY', type: 'select', proxies: [shared], now: shared, 'include-all-providers': true, filter: 'keep' }
], rules: [`MATCH,${shared}`] });
assert.deepStrictEqual(group(providerMigration, 'PROXY').use, ['airport']);
assert.deepStrictEqual(group(providerMigration, 'PROXY').proxies, []);
assert.strictEqual(group(providerMigration, 'PROXY')['include-all-providers'], true);
assert.strictEqual(group(providerMigration, 'PROXY').now, undefined);
assert.strictEqual(group(providerMigration, 'PROXY').filter, 'keep');

const noOrdinaryMigration = apply({ proxies: [node('A')], 'proxy-groups': [
  { name: shared, type: 'select', proxies: ['A'] },
  { name: 'Auto', type: 'url-test', proxies: [shared] }
], rules: [`MATCH,${shared}`] });
assert.deepStrictEqual(group(noOrdinaryMigration, 'Auto').proxies, ['A']);
assert(noOrdinaryMigration.rules.includes('MATCH,A'));
assert(!JSON.stringify(noOrdinaryMigration).includes(shared));

const internal = apply({ proxies: [node('A')], 'proxy-groups': [
  { name: probeName, type: 'select', proxies: ['A'] },
  { name: 'Internal', type: 'select', proxies: ['A'], hidden: true },
  { name: 'Main', type: 'select', proxies: ['A'] }
], rules: ['MATCH,Internal'] });
assert(internal.rules.includes('DOMAIN,gemini.google.com,Main'));

const notices = apply({ proxies: [node('A'), node('消息: 通知')], 'proxy-groups': [
  { name: 'PROXY', type: 'select', proxies: ['A', '消息: 通知'] }
], rules: ['MATCH,PROXY'] });
assert.deepStrictEqual(group(notices, 'PROXY').proxies, ['A']);
assert.deepStrictEqual(group(notices, probeName).proxies, ['A']);

for (const value of [
  { proxies: [node('A')], 'proxy-groups': [{ name: 'Auto', type: 'url-test', proxies: ['A'] }], rules: ['MATCH,Auto'] },
  { 'proxy-providers': providers, rules: ['MATCH,DIRECT'] }
]) {
  const result = apply(value);
  assert.strictEqual(result['proxy-groups'].filter(x => x.type === 'select' && x.name !== probeName).length, 0);
  assert.deepStrictEqual(result.rules, value.rules, 'no ordinary selector: retain original routing');
  assert.strictEqual(group(result, probeName).hidden, true);
}
console.log('Enhancement recovery-group tests passed');
