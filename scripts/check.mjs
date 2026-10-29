import { readdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', Marketplaces__StockSource__BaseUrl: '', Marketplaces__StockSource__AllowWrites: 'false', Synchronization__IncludeRealStores: 'false', Synchronization__AllowRealWrites: 'false' };
const failures = [];
// Tests use console output; Windows Event Log permissions must not break cookie key creation.
env.Logging__EventLog__LogLevel__Default = 'None';
// Tests must never inherit live marketplace credentials from the operator's shell.
for(const key of Object.keys(env))if(/^(MarketplaceAccounts__|Enterprise__|Businesses__)/i.test(key))delete env[key];
env.Enterprise__AllowDemoExchange = 'false';
for (const market of ['Trendyol','Hepsiburada','Amazon','N11','Boyner','Ticimax'])
  for (const mode of ['stage','production'])
    for (const key of ['SellerId','ApiKey','ApiSecret','AppKey','AppSecret','MerchantId','Username','Password','ClientId','ClientSecret','RefreshToken','UyeKodu','AllowWrites'])
      env[`Marketplaces__${market}__${mode}__${key}`] = key === 'AllowWrites' ? 'false' : '';
function run(exe, args) {
  const result = spawnSync(exe, args, { env, stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) failures.push(args.join(' '));
}
run('dotnet', ['build', 'src/MarketPilot.Web']);
if (failures.length) process.exit(1);
for (const entry of readdirSync('tests', { withFileTypes: true }).filter(e => e.isDirectory() && /^MarketPilot\..*Checks$/.test(e.name)).sort((a,b) => a.name.localeCompare(b.name)))
  run('dotnet', ['run', '--project', 'tests/' + entry.name, '-p:BuildProjectReferences=false']);
for (const entry of readdirSync('tests').filter(name => name.endsWith('-checks.cjs')).sort())
  run(process.execPath, ['tests/' + entry]);
run(process.execPath, ['tests/tenant-isolation-audit.cjs']);
run('dotnet', ['run', '--project', 'tests/MarketPilot.WorkerIsolationAudit', '-p:BuildProjectReferences=false']);
if (failures.length) {
  console.error('FAILED checks:\n' + failures.map(name => '  ' + name).join('\n'));
  process.exit(1);
}
console.log('PASS all MarketPilot checks. Temporary test data only; no live marketplace credentials.');
