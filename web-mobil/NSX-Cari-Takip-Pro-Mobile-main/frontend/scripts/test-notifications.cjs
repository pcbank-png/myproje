// Runs real notification modules with native/network adapters replaced.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const ts = require('typescript');
const root = path.resolve(__dirname, '..');

function setup(overrides = {}) {
  const cache = new Map();
  const storage = new Map();
  const scheduled = [];
  const order = [];
  let handler;
  let remote = [];
  let failSchedule = false;
  const notifications = {
    AndroidImportance: { HIGH: 4, MAX: 5 },
    AndroidNotificationPriority: { HIGH: 'high' },
    setNotificationChannelAsync: async () => { order.push('channel'); },
    setNotificationHandler: (h) => { handler = h; },
    getPermissionsAsync: async () => { order.push('permission'); return { status: 'granted' }; },
    getExpoPushTokenAsync: async () => ({ data: 'ExponentPushToken[test]' }),
    scheduleNotificationAsync: async (request) => {
      if (failSchedule) throw new Error('native unavailable');
      scheduled.push(request);
      return 'local-id';
    },
  };
  const api = { isMockMode: false, nsxApi: {
    getInbox: async () => remote,
    registerPushToken: async () => { order.push('register'); },
  } };
  let session = { mode: 'live', tenantId: 'tenant', mobileDeviceId: 'device' };
  const sessionListeners = new Set();
  const mocks = {
    react: {},
    'react-native': { Platform: { OS: 'android' } },
    'expo-constants': { expoConfig: { extra: { eas: { projectId: 'test-project' } } } },
    'expo-notifications': notifications,
    '@react-native-async-storage/async-storage': {
      getItem: async (key) => storage.get(key) ?? null,
      setItem: async (key, value) => { storage.set(key, value); },
      removeItem: async (key) => storage.delete(key),
    },
    '@/src/api': api,
    '@/src/auth/session': { getSessionSync: () => session, subscribeSession: (listener) => { sessionListeners.add(listener); return () => sessionListeners.delete(listener); } },
    ...overrides,
  };
  function load(name, from = root) {
    if (mocks[name]) return mocks[name];
    let file = name.startsWith('@/') ? path.join(root, name.slice(2)) : path.resolve(from, name);
    if (!path.extname(file)) file += '.ts';
    if (cache.has(file)) return cache.get(file).exports;
    const module = { exports: {} };
    cache.set(file, module);
    const js = ts.transpileModule(fs.readFileSync(file, 'utf8'), {
      compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020, esModuleInterop: true, jsx: ts.JsxEmit.ReactJSX },
    }).outputText;
    const fn = vm.runInThisContext(`(function(require,module,exports,process){${js}\n})`, { filename: file });
    fn((dep) => load(dep, path.dirname(file)), module, module.exports, process);
    return module.exports;
  }
  return { load, scheduled, order, storage, getHandler: () => handler,
    setSession: (value) => { session=value; sessionListeners.forEach(listener=>listener(value)); },
    setRemote: (rows) => { remote = rows; }, setFailure: (value) => { failSchedule = value; } };
}

const message = (id, category = 'debts') => ({
  id, category, title: 'Yeni kayıt', body: 'Müşteri · ₺100,00', read: false,
  createdAtUtc: new Date().toISOString(),
});

async function main() {
  const env = setup();
  const inbox = env.load('@/src/notifications/inbox');
  const push = env.load('@/src/notifications/push-register');
  push.configureNotificationHandler();
  await Promise.all([
    inbox.receiveInboxMessage(message('debt')),
    inbox.receiveInboxMessage(message('collection', 'collections')),
    inbox.receiveInboxMessage(message('reminder', 'reminders')),
  ]);
  assert.equal(env.scheduled.length, 3, 'every category presents a banner');
  assert.equal(JSON.parse(env.storage.get('nsx.inbox.v2.live.tenant.device')).length, 3, 'simultaneous events retain all rows');
  assert.equal(env.scheduled[2].trigger.channelId, 'nsx-reminders');
  assert.equal(env.scheduled[0].trigger.channelId, 'nsx-cari');
  await inbox.receiveInboxMessage(message('debt'));
  assert.equal(env.scheduled.length, 3, 'repeated event does not show twice');
  const handler = env.getHandler().handleNotification;
  const remoteEvent = (id) => ({ request: { content: { data: { messageId: id } } } });
  assert.equal((await handler(remoteEvent('debt'))).shouldShowBanner, false, 'late push is suppressed');
  assert.equal((await handler(remoteEvent('push-first'))).shouldShowBanner, true);
  await inbox.receiveInboxMessage(message('push-first'));
  assert.equal(env.scheduled.length, 3, 'push before realtime does not duplicate');
  assert.equal((await handler({ request: { content: { data: env.scheduled[0].content.data } } })).shouldShowBanner, true,
    'local fallback itself stays visible');
  env.setFailure(true);
  await inbox.receiveInboxMessage(message('retry'));
  env.setFailure(false);
  await inbox.receiveInboxMessage(message('retry'));
  assert.equal(env.scheduled.length, 4, 'failed native presentation can retry');

  const clean = setup();
  const cleanInbox = clean.load('@/src/notifications/inbox');
  clean.setRemote([message('old')]);
  await cleanInbox.refreshInboxFromServer({ notify: false });
  assert.equal(clean.scheduled.length, 0, 'initial hydration is silent');
  clean.setRemote([message('new'), message('old')]);
  await cleanInbox.refreshInboxFromServer({ notify: true });
  assert.equal(clean.scheduled.length, 1, 'poll fallback presents newly committed rows');
  const cleanPush = clean.load('@/src/notifications/push-register');
  assert.equal((await cleanPush.registerPushTokenWithServer()).status, 'registered');
  assert.ok(clean.order.indexOf('channel') < clean.order.indexOf('permission'), 'Android channels precede permission request');
  assert.equal(clean.load('@/src/notifications/notification-prefs').DEFAULT_NOTIFICATION_PREFS.debts, true);
  console.log('PASS: realtime categories, concurrent inbox writes, Android channels, push dedupe, failed presentation retry, silent hydration, polling fallback, token registration, debt defaults');
  await testNotificationTaps();
}

async function testNotificationTaps() {
  // Small hook runner: native adapters are mocked, the production component runs.
  const cells = [];
  let cursor = 0;
  let effects = [];
  const react = {
    useState(initial) {
      const index = cursor++;
      if (!cells[index]) cells[index] = { value: initial };
      return [cells[index].value, (value) => { cells[index].value = value; }];
    },
    useRef(initial) {
      const index = cursor++;
      if (!cells[index]) cells[index] = { current: initial };
      return cells[index];
    },
    useEffect(effect, deps) {
      const index = cursor++;
      const previous = cells[index];
      if (!previous || deps.some((dep, i) => !Object.is(dep, previous.deps[i]))) {
        effects.push(() => {
          previous?.cleanup?.();
          cells[index] = { deps, cleanup: effect() };
        });
      }
    },
  };
  let response = null;
  let auth = { ready: false, session: null };
  let navigation;
  let segments = [];
  let received;
  let clears = 0;
  let appStateChanged;
  let holdInteractions = false;
  const interactionTasks = [];
  const appState = {
    currentState: 'active',
    addEventListener: (name, callback) => { appStateChanged = callback; return { remove() {} }; },
  };
  const flushInteractions = () => {
    const tasks = interactionTasks.splice(0);
    for (const task of tasks) if (!task.cancelled) task.callback();
  };
  const routes = [];
  const router = { navigate(route) {
    const rows = JSON.parse(env.storage.get('nsx.inbox.v2.live.tenant.device'));
    assert.ok(rows.some((row) => row.id === route.params.id), 'detail data stored before navigation');
    routes.push(route);
  } };
  const env = setup({
    react,
    'react-native': {
      Platform: { OS: 'ios' }, AppState: appState,
      InteractionManager: { runAfterInteractions(callback) {
        const task = { callback, cancelled: false };
        interactionTasks.push(task);
        return { cancel() { task.cancelled = true; } };
      } },
    },
    'react/jsx-runtime': { jsx: () => null },
    'expo-router': {
      useRouter: () => router, useRootNavigationState: () => navigation, useSegments: () => segments,
    },
    '@/src/auth/auth-context': { useAuth: () => auth },
    'expo-notifications': {
      DEFAULT_ACTION_IDENTIFIER: 'default',
      useLastNotificationResponse: () => response,
      addNotificationReceivedListener: (callback) => { received = callback; return { remove() {} }; },
      clearLastNotificationResponseAsync: async () => { clears++; response = null; },
    },
  });
  const component = env.load('@/src/notifications/push-inbox-sync.tsx').NativePushInboxSync;
  const render = () => {
    cursor = 0; effects = []; component(); effects.forEach((effect) => effect());
    if (!holdInteractions) flushInteractions();
  };
  const settle = async () => { await new Promise(setImmediate); render(); };
  const tap = (id, category) => ({ actionIdentifier: 'default', notification: {
    date: Date.now(), request: { identifier: `request-${id}`, content: {
      title: 'Bildirim', body: 'İçerik', data: { tenantId: "tenant", messageId: id, category },
    } },
  } });
  response = tap('cold-start', 'reminders');
  render(); await settle();
  assert.equal(routes.length, 0, 'cold start waits for auth and navigation');
  navigation = { key: 'root' }; auth = { ready: true, session: null }; segments = ['login'];
  render(); assert.equal(routes.length, 0, 'logged-out tap remains pending');
  auth = { ready: true, session: { tenantId: 'tenant' } };
  render(); assert.equal(routes.length, 0, 'waits for login redirect to settle');
  segments = ['(tabs)']; render(); await settle();
  assert.equal(routes[0].params.id, 'cold-start');
  assert.equal(routes[0].pathname, '/ayarlar/bildirim/[id]');
  assert.equal(clears, 1, 'consumed launch response cleared');
  const first = tap('warm-debt', 'debts');
  response = first; render(); await settle();
  assert.equal(routes[1].params.id, 'warm-debt');
  response = first; render(); await settle();
  assert.equal(routes.length, 2, 'same response does not navigate twice');
  response = tap('warm-collection', 'collections'); render(); await settle();
  assert.equal(routes[2].params.id, 'warm-collection');
  response = tap('warm-system', 'system'); render(); await settle();
  assert.equal(routes[3].params.id, 'warm-system');
  response = tap('older-tap', 'debts'); render();
  response = tap('latest-tap', 'reminders'); render(); await settle();
  assert.equal(routes.at(-1).params.id, 'latest-tap', 'latest tap wins asynchronous preparation race');
  assert.ok(!routes.some((route) => route.params.id === 'older-tap'));
  const plain = tap('no-id', 'system').notification;
  plain.request.content.data = { tenantId: "tenant" };
  received(plain); await settle();
  response = { actionIdentifier: 'default', notification: plain }; render(); await settle();
  assert.equal(routes.at(-1).params.id, 'push-request-no-id', 'id-less local notification opens stable detail');

  const beforeReminder = routes.length;
  appState.currentState = 'inactive'; appStateChanged('inactive');
  response = tap('ios-reminder', 'reminders');
  // Some native responses omit or serialize an invalid delivery date.
  response.notification.date = 'not-a-date';
  render(); await settle();
  assert.equal(routes.length, beforeReminder, 'iOS inactive reminder tap must not navigate');
  holdInteractions = true;
  appState.currentState = 'active'; appStateChanged('active'); render();
  assert.equal(routes.length, beforeReminder, 'wait for existing native interactions after activation');
  flushInteractions();
  assert.equal(routes.at(-1).params.id, 'ios-reminder');
  assert.ok(Number.isFinite(Date.parse(JSON.parse(env.storage.get('nsx.inbox.v2.live.tenant.device'))[0].createdAt)),
    'invalid native timestamp is stored as a valid fallback timestamp');
  holdInteractions = false;

  const beforeDismiss = routes.length;
  response = { ...tap('dismissed', 'reminders'), actionIdentifier: 'dismiss' };
  render(); await settle();
  assert.equal(routes.length, beforeDismiss, 'dismissal must not open notification detail');

  holdInteractions = true;
  response = tap('cancel-on-background', 'reminders'); render(); await settle();
  appState.currentState = 'background'; appStateChanged('background'); render();
  flushInteractions();
  assert.equal(routes.length, beforeDismiss, 'queued navigation is cancelled on backgrounding');
  appState.currentState = 'active'; appStateChanged('active'); render(); flushInteractions();
  assert.equal(routes.at(-1).params.id, 'cancel-on-background', 'pending tap resumes when app becomes active');
  console.log('PASS: notification taps across all categories, cold start, login readiness, store-before-route, duplicate response, rapid taps, local notification without messageId, iOS activation/interaction gating, invalid timestamp, dismissal, background cancellation');
}
module.exports = { setup };
if (require.main === module) main().catch((error) => { console.error(error); process.exitCode = 1; });
