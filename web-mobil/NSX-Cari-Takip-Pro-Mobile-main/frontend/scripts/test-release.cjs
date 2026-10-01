const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const ts = require('typescript');
const { setup } = require('./test-notifications.cjs');
const root = path.resolve(__dirname, '..');
function moduleWithMocks(file, mocks = {}) {
  const module = { exports: {} };
  const source = ts.transpileModule(fs.readFileSync(path.join(root,file),'utf8'), {
    compilerOptions:{module:ts.ModuleKind.CommonJS,target:ts.ScriptTarget.ES2020,esModuleInterop:true},
  }).outputText;
  vm.runInThisContext(`(function(require,module,exports,process){${source}\n})`)(name => {
    if (name in mocks) return mocks[name];
    throw new Error(`Unexpected import ${name}`);
  },module,module.exports,{env:{EXPO_PUBLIC_NSX_MODE:'live'}});
  return module.exports;
}
const row = (id) => ({ id,category:'debts',title:id,body:'test',createdAtUtc:new Date().toISOString(),read:false });
async function main() {
  const { parseMoney } = moduleWithMocks('src/utils/parse-money.ts');
  for (const [input,expected] of [['100.50',100.5],['100,50',100.5],['1.234,56',1234.56],['1000',1000],['0,01',0.01]]) assert.equal(parseMoney(input).amount,expected,input);
  for (const input of ['12abc','1,2,3','1.234','12.3456','-10','0','1e3','Infinity','9999999999999']) assert.ok(parseMoney(input).error,input);
  assert.equal(parseMoney('999999999999').amount,999999999999);

  let failDelete = true, releaseDelete, deleteInput;
  const api = { isMockMode:false,nsxApi:{
    getInbox:async()=>[],getInboxMessage:async()=>{throw Error('not found');},markInboxRead:async()=>{},
    deleteInbox:async(input)=>{deleteInput=input;if(failDelete)throw Error('offline');await new Promise(resolve=>releaseDelete=resolve);},
  }};
  const env=setup({'@/src/api':api}); const inbox=env.load('@/src/notifications/inbox');
  await inbox.receiveInboxMessage(row('first'));
  await assert.rejects(inbox.deleteInboxItem('first'),/offline/);
  assert.equal((await inbox.getInboxItem('first')).id,'first','failed deletion preserves row');
  failDelete=false;
  const deletion=inbox.deleteInboxItem('first');
  while(!releaseDelete) await new Promise(resolve=>setImmediate(resolve));
  const incoming=inbox.receiveInboxMessage(row('during-delete')); releaseDelete();
  await Promise.all([deletion,incoming]);
  assert.equal(await inbox.getInboxItem('first'),null);
  assert.equal((await inbox.getInboxItem('during-delete')).id,'during-delete','concurrent incoming row survives');
  releaseDelete=undefined;
  const clearing=inbox.clearInbox();
  while(!releaseDelete) await new Promise(resolve=>setImmediate(resolve));
  assert.equal(deleteInput.all,true,'bulk clear includes server history');
  assert.ok(deleteInput.createdBeforeUtc,'bulk clear is bounded by snapshot time');
  const duringClear=inbox.receiveInboxMessage(row('during-clear'));releaseDelete();
  await Promise.all([clearing,duringClear]);
  assert.equal((await inbox.getInboxItem('during-clear')).id,'during-clear');
  env.setSession({mode:'live',tenantId:'other',mobileDeviceId:'device'});
  assert.equal(await inbox.getInboxItem('during-delete'),null,'other account cannot see old rows');
  await inbox.receiveInboxMessage(row('other-account'));
  assert.ok(env.storage.has('nsx.inbox.v2.live.other.device'));
  env.setSession(null); assert.equal(await inbox.getInboxItem('other-account'),null);

  let resolveOld;
  const stale=setup({'@/src/api':{isMockMode:false,nsxApi:{getInbox:()=>new Promise(resolve=>resolveOld=resolve),getInboxMessage:async()=>{throw Error('not found');}}}});
  const staleInbox=stale.load('@/src/notifications/inbox'); const request=staleInbox.refreshInboxFromServer({notify:true});
  while(!resolveOld) await new Promise(resolve=>setImmediate(resolve));
  stale.setSession({mode:'live',tenantId:'new-tenant',mobileDeviceId:'device'});
  resolveOld([row('old-server-response')]); await request;
  assert.equal(await staleInbox.getInboxItem('old-server-response'),null,'late old response cannot enter new account');
  assert.equal(stale.scheduled.length,0,'old account cannot present a banner after switch');

  let failStorage=false, failures=0, releaseWrite;
  const storage=new Map();
  const session=moduleWithMocks('src/auth/session.ts',{
    'react-native':{Platform:{OS:'ios'}},'expo-secure-store':{
      getItemAsync:async(key)=>storage.get(key)??null,
      setItemAsync:async(key,value)=>{if(failStorage){failures++;throw Error('keychain');}if(JSON.parse(value || '{}').companyId === 'pending') await new Promise(resolve=>releaseWrite=resolve);storage.set(key,value);},
      deleteItemAsync:async(key)=>storage.delete(key),
    },
  });
  const value={mode:'live',tenantId:'tenant',mobileDeviceId:'device',companyId:'company',companyName:'Test',accessToken:'a',refreshToken:'r',accessTokenExpiresAt:'2099-01-01',refreshTokenExpiresAt:'2099-01-01'};
  await session.saveSession(value);
  failStorage=true;
  await assert.rejects(session.saveSession({...value,tenantId:'second'}));
  assert.equal(session.getSessionSync().tenantId,'tenant','failed pairing is not committed in memory');
  await assert.rejects(session.updateTokens({...value,accessToken:'new-a',refreshToken:'new-r'},'r'));
  assert.equal(session.getSessionSync().refreshToken,'new-r','never replay already rotated token');
  assert.equal(storage.has('nsx.session.v2.live'),false,'old persisted token removed on rotation storage failure');
  assert.equal(failures,6,'both writes retried three times');
  failStorage=false;await session.clearSession();assert.equal(session.getSessionSync(),null);
  await session.saveSession(value);
  const staleEdit=assert.rejects(session.setActiveCompany('pending'),/Oturum/);
  while(!releaseWrite) await new Promise(resolve=>setImmediate(resolve));
  const logout=session.clearSession();
  const pairing=session.saveSession({...value,tenantId:'new-account',refreshToken:'new-account-token'});
  releaseWrite();await Promise.all([staleEdit,logout,pairing]);
  assert.equal(session.getSessionSync().tenantId,'new-account','late write cannot resurrect logged-out account');
  assert.equal(JSON.parse(storage.get('nsx.session.v2.live')).tenantId,'new-account');
  console.log('PASS: strict money parsing, failed and concurrent inbox edits, account isolation, stale responses, persistent session failures');
}
main().catch(error=>{console.error(error);process.exitCode=1;});
