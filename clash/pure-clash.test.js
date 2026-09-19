const assert = require('node:assert/strict');
const { main } = require('./pure-clash');

const node = (name, server = 'example.com') => ({ name, type: 'ss', server });
const group = (config, name) => config['proxy-groups'].find(item => item.name === name);

{
  const config = {
    proxies: [node('香港 I1'), node('日本 I0')],
    'proxy-groups': [{ name: '原有选择', type: 'select', proxies: ['香港 I1'] }],
    rules: ['DOMAIN-SUFFIX,steamcontent.com,DIRECT', 'MATCH,原有选择']
  };
  main(config);
  assert.equal(group(config, '🌐 自动稳定节点').type, 'url-test');
  assert.deepEqual(group(config, '🌐 自动稳定节点').proxies, ['香港 I1', '日本 I0']);
  assert.equal(group(config, '🌐 自动稳定节点').tolerance >= 50, true);
  assert.equal(group(config, '🧭 节点模式').proxies[0], '🌐 自动稳定节点');
  assert.equal(group(config, '原有选择').proxies[0], '香港 I1');
  assert.equal(config.rules.at(-1), 'MATCH,原有选择');
  assert(config.rules.indexOf('DOMAIN-SUFFIX,steamcontent.com,DIRECT') < config.rules.findIndex(rule => rule.includes('steampowered.com')));
  assert(!config.listeners);
  assert.equal(config.ipv6, false);
  assert.equal(config.dns.ipv6, false);
  assert(config.rules.includes('DOMAIN-SUFFIX,cm.steampowered.com,DIRECT'));
  const once = JSON.stringify(config);
  main(config);
  assert.equal(JSON.stringify(config), once);
}

{
  const config = {
    'proxy-providers': { Airport: { type: 'http', url: 'https://example.com/sub', path: './airport.yaml' } },
    'proxy-groups': [], rules: ['MATCH,DIRECT']
  };
  main(config);
  assert.deepEqual(group(config, '🌐 自动稳定节点').use, ['Airport']);
  assert.deepEqual(group(config, '🧭 节点模式').use, ['Airport']);
  assert.equal(config['proxy-providers'].Airport['health-check'].enable, true);
}

{
  const config = { proxies: [node('单节点')], 'proxy-groups': [], rules: [] };
  main(config);
  assert.equal(group(config, '🌐 自动稳定节点'), undefined);
  assert.deepEqual(group(config, '🧭 节点模式').proxies, ['单节点']);
}

{
  const config = { proxies: [], 'proxy-groups': [{ name: '原有选择', type: 'select', proxies: ['DIRECT'] }], rules: ['MATCH,原有选择'] };
  main(config);
  assert.deepEqual(config['proxy-groups'], [{ name: '原有选择', type: 'select', proxies: ['DIRECT'] }]);
  assert.deepEqual(config.rules, ['MATCH,原有选择']);
  assert.equal(config.ipv6, false);
}

console.log('pure-clash tests passed');
