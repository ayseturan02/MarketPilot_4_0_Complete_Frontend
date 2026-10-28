import { readdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
const env = { ...process.env, ASPNETCORE_ENVIRONMENT: 'Production', Marketplaces__StockSource__BaseUrl: '', Marketplaces__StockSource__AllowWrites: 'false', Synchronization__IncludeRealStores: 'false', Synchronization__AllowRealWrites: 'false' };
// Tests must never inherit live marketplace credentials from the operator's shell.
for(const key of Object.keys(env))if(/^MarketplaceAccounts__/i.test(key))delete env[key];
for (const market of ['Trendyol','Hepsiburada','Amazon','N11','Boyner','Ticimax'])
  for (const mode of ['stage','production'])
    for (const key of ['SellerId','ApiKey','ApiSecret','AppKey','AppSecret','MerchantId','Username','Password','ClientId','ClientSecret','RefreshToken','UyeKodu','AllowWrites'])
      env[`Marketplaces__${market}__${mode}__${key}`] = key === 'AllowWrites' ? 'false' : '';
function run(exe, args) {
  const result = spawnSync(exe, args, { env, stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status || 1);
}
run('dotnet', ['build', 'src/MarketPilot.Web']);
for (const entry of readdirSync('tests', { withFileTypes: true }).filter(e => e.isDirectory() && /^MarketPilot\..*Checks$/.test(e.name)).sort((a,b) => a.name.localeCompare(b.name)))
  run('dotnet', ['run', '--project', 'tests/' + entry.name, '-p:BuildProjectReferences=false']);
for (const entry of readdirSync('tests').filter(name => name.endsWith('-checks.cjs')).sort())
  run(process.execPath, ['tests/' + entry]);
console.log('PASS all MarketPilot checks. Temporary test data only; no live marketplace credentials.');
