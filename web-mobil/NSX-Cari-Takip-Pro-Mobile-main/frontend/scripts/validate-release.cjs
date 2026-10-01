const fs = require('node:fs');
const path = require('node:path');
const envFile = path.join(__dirname, '..', '.env');
if (fs.existsSync(envFile)) process.loadEnvFile(envFile);
if (process.env.EAS_BUILD_PROFILE === 'production') {
  if (process.env.EXPO_PUBLIC_NSX_MODE !== 'live') throw new Error('Production requires live API mode');
  const url = new URL(process.env.EXPO_PUBLIC_NSX_BASE_URL ?? '');
  if (url.protocol !== 'https:' || ['localhost','127.0.0.1'].includes(url.hostname)) throw new Error('Production requires a public HTTPS API URL');
  const config = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'app.json'), 'utf8'));
  const projectId = process.env.EXPO_PUBLIC_EAS_PROJECT_ID ?? config.expo.extra?.eas?.projectId;
  if (!/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(projectId ?? '')) throw new Error('EAS projectId required');
}
