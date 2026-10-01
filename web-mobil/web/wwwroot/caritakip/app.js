"use strict";
const api="/Api/CaritakipCloud";
const isDemo=location.protocol==="file:"||new URLSearchParams(location.search).get("demo")==="1";
const state={customers:[],selected:null,transactions:[],recentTransactions:[],companies:[],cursor:0,ready:false,tab:"home",companyName:"",transactionCustomer:null,demoTransactions:[],pickerType:null};
let pollBusy=false,searchTimer;
const $=id=>document.getElementById(id);
const translations={
  tr:{
    "aria.home":"Anasayfa","aria.language":"Dil seç","aria.profile":"Personel, Profil ve Ayarlar","aria.refresh":"Yenile","aria.mainMenu":"Ana menü","aria.close":"Kapat",
    panelLabel:"Cari Yönetim Paneli",retryConnection:"Bağlantıyı yeniden dene",loginRequired:"Güvenli giriş gerekli",loginHelp:"Bu tarayıcıda geçerli oturum bulunamadı. Masaüstü programdan yeni mobil bağlantı QR kodu açın.",
    financeTitle:"CARİ FİNANS ÖZETİ",financeSubtitle:"İşletmenizin güncel cari finans durumu",currentBalance:"Güncel Bakiye",allAccounts:"Tüm Hesaplar",totalReceivable:"Toplam Alacak",totalCollected:"Toplam Tahsilat",remainingBalance:"Kalan Bakiye",
    customers:"Müşteriler",customersQuick:"Müşterilerinizi yönetin ve görüntüleyin",newCustomer:"Yeni Müşteri Ekle",newCustomerQuick:"Hızlıca yeni müşteri kaydı oluşturun",collectPayment:"Tahsilat Yap",collectPaymentQuick:"Müşterinizden tahsilat kaydedin",addDebt:"Borç Ekle",addDebtQuick:"Müşterinize borç kaydı oluşturun",
    recentMovements:"Son Hareketler",recentMovementsSub:"Son işlemleriniz",viewAll:"Tümünü Gör",customerManagement:"CARİ YÖNETİMİ",newShort:"Yeni",searchCustomer:"Ad, cari kod veya telefon ara…",selectCustomerDetail:"Detay ve hareketleri görmek için bir müşteri seçin.",
    accountMovements:"CARİ HAREKETLER",movements:"Hareketler",movementsSub:"Borç ve tahsilat hareketlerini tek ekranda izleyin.",mobilePanel:"MOBİL PANEL",settings:"Ayarlar",settingsSub:"Oturum ve bağlantı ayarlarınızı yönetin.",
    connection:"Bağlantı",connectionActive:"● Bağlantı Aktif",customerCount:"Müşteri Sayısı",panelMode:"Panel Modu",refreshData:"Verileri Yenile",refreshDataSub:"Sistemdeki son kayıtları şimdi getir",logout:"Oturumu Kapat",logoutSub:"Bu cihazdaki mobil oturumu güvenle sonlandır",important:"Önemli:",logoutWarning:"Oturumu kapattığınızda bu cihazın bağlantısı sonlandırılır. Yeniden erişmek için ana NSX Cari Takip Pro programından QR kod ile tekrar bağlantı kurmanız gerekir.",
    home:"Anasayfa",accountCard:"CARİ KART",company:"Firma",accountCode:"Cari kod",accountCodePlaceholder:"Kaydedildiğinde otomatik oluşturulur",nameTitle:"Ad / unvan",phone:"Telefon",email:"E-posta",address:"Adres",notes:"Not",cancel:"Vazgeç",save:"Kaydet",selectCustomer:"MÜŞTERİ SEÇ",accountMovement:"CARİ HAREKET",customer:"Müşteri",amount:"Tutar",date:"Tarih",description:"Açıklama",delete:"Sil",
    operationFailed:"İşlem tamamlanamadı.",sessionRetry:"Oturum kapatılmadı; bağlantı yeniden denenecek.",internetWaiting:"İnternet bağlantısı bekleniyor. Oturum kapatılmadı; bağlantı gelince devam edilecek.",noAccountMovement:"Henüz cari hareket bulunmuyor.",debt:"Borç",collection:"Tahsilat",noMovement:"Henüz hareket bulunmuyor.",localDemo:"Yerel Demo",live:"Canlı",
    matchingCustomers:"{count} eşleşen müşteri.",recentCustomersShown:"Son işlem gören {count} müşteri gösteriliyor. Diğer müşteriler için arama yapın.",unnamed:"İsimsiz",customerNotFound:"Müşteri bulunamadı.",balance:"Bakiye:",edit:"Düzenle",noCustomerMovement:"Cari hareket bulunmuyor.",
    companySyncRequired:"Önce masaüstündeki firma kaydını sistemle eşitleyin.",selectCompany:"Firma seçin",companyFallback:"Firma",editCustomer:"Müşteriyi Düzenle",newCustomerTitle:"Yeni Müşteri",addCustomerFirst:"İşlem için önce müşteri ekleyin.",debtCustomerPick:"Borç eklenecek müşteri",collectionCustomerPick:"Tahsilat yapılacak müşteri",matchingCustomersShort:"{count} eşleşen müşteri",recentCustomersShort:"Son işlem gören {count} müşteri",
    transactionAdd:"Ekle",transactionEdit:"Düzenle",customerSelectFailed:"Müşteri seçilemedi.",customerSaved:"Müşteri kaydedildi.",recordConflict:"Kayıt başka bir cihazda değişti. Liste yenileniyor.",debtSaved:"Borç kaydedildi.",collectionSaved:"Tahsilat kaydedildi.",confirmDeleteCustomer:"Müşteri silinsin mi? Hareketler geçmiş ve senkronizasyon için korunur.",customerDeleted:"Müşteri silindi.",confirmDeleteMovement:"Bu hareket silinsin mi?",movementDeleted:"Hareket silindi.",dataUpdated:"Veriler güncellendi.",demoLogoutDisabled:"Yerel demo modunda oturum kapatma devre dışı.",logoutFailed:"Çıkış tamamlanamadı. İnternet bağlantısıyla yeniden deneyin."
  },
  en:{
    "aria.home":"Home","aria.language":"Select language","aria.profile":"Staff, Profile and Settings","aria.refresh":"Refresh","aria.mainMenu":"Main menu","aria.close":"Close",
    panelLabel:"Account Management Panel",retryConnection:"Retry connection",loginRequired:"Secure sign-in required",loginHelp:"No valid session was found in this browser. Open a new mobile connection QR code from the desktop application.",
    financeTitle:"ACCOUNT FINANCE SUMMARY",financeSubtitle:"Current account position of your business",currentBalance:"Current Balance",allAccounts:"All Accounts",totalReceivable:"Total Receivable",totalCollected:"Total Collected",remainingBalance:"Remaining Balance",
    customers:"Customers",customersQuick:"View and manage your customers",newCustomer:"Add New Customer",newCustomerQuick:"Create a new customer record quickly",collectPayment:"Collect Payment",collectPaymentQuick:"Record a payment from your customer",addDebt:"Add Debt",addDebtQuick:"Create a debt record for your customer",
    recentMovements:"Recent Activity",recentMovementsSub:"Your latest transactions",viewAll:"View All",customerManagement:"ACCOUNT MANAGEMENT",newShort:"New",searchCustomer:"Search name, account code or phone…",selectCustomerDetail:"Select a customer to view details and transactions.",
    accountMovements:"ACCOUNT ACTIVITY",movements:"Activity",movementsSub:"View debt and collection transactions in one place.",mobilePanel:"MOBILE PANEL",settings:"Settings",settingsSub:"Manage your session and connection settings.",
    connection:"Connection",connectionActive:"● Connection Active",customerCount:"Customer Count",panelMode:"Panel Mode",refreshData:"Refresh Data",refreshDataSub:"Load the latest records from the system",logout:"Sign Out",logoutSub:"Securely end the mobile session on this device",important:"Important:",logoutWarning:"Signing out will end this device connection. To access the panel again, you must reconnect using a QR code from the main NSX Cari Takip Pro application.",
    home:"Home",accountCard:"ACCOUNT CARD",company:"Company",accountCode:"Account code",accountCodePlaceholder:"Created automatically when saved",nameTitle:"Name / title",phone:"Phone",email:"Email",address:"Address",notes:"Notes",cancel:"Cancel",save:"Save",selectCustomer:"SELECT CUSTOMER",accountMovement:"ACCOUNT ACTIVITY",customer:"Customer",amount:"Amount",date:"Date",description:"Description",delete:"Delete",
    operationFailed:"The operation could not be completed.",sessionRetry:"The session remains active; the connection will be retried.",internetWaiting:"Waiting for an internet connection. The session remains active and will continue when the connection returns.",noAccountMovement:"No account activity yet.",debt:"Debt",collection:"Collection",noMovement:"No activity yet.",localDemo:"Local Demo",live:"Live",
    matchingCustomers:"{count} matching customers.",recentCustomersShown:"Showing the {count} most recently active customers. Use search for other customers.",unnamed:"Unnamed",customerNotFound:"Customer not found.",balance:"Balance:",edit:"Edit",noCustomerMovement:"No account activity found.",
    companySyncRequired:"Sync a company record from the desktop application first.",selectCompany:"Select company",companyFallback:"Company",editCustomer:"Edit Customer",newCustomerTitle:"New Customer",addCustomerFirst:"Add a customer before creating a transaction.",debtCustomerPick:"Customer for debt entry",collectionCustomerPick:"Customer for collection",matchingCustomersShort:"{count} matching customers",recentCustomersShort:"{count} most recently active customers",
    transactionAdd:"Add",transactionEdit:"Edit",customerSelectFailed:"Customer could not be selected.",customerSaved:"Customer saved.",recordConflict:"This record was changed on another device. Refreshing the list.",debtSaved:"Debt saved.",collectionSaved:"Collection saved.",confirmDeleteCustomer:"Delete this customer? Transaction history is retained for history and synchronization.",customerDeleted:"Customer deleted.",confirmDeleteMovement:"Delete this transaction?",movementDeleted:"Transaction deleted.",dataUpdated:"Data refreshed.",demoLogoutDisabled:"Sign out is disabled in local demo mode.",logoutFailed:"Sign out could not be completed. Check your internet connection and try again."
  }
};
let currentLang="tr";try{currentLang=localStorage.getItem("nsx-cari-lang")==="en"?"en":"tr"}catch{}
const t=(key,vars={})=>{let value=translations[currentLang]?.[key]??translations.tr[key]??key;for(const [name,replacement] of Object.entries(vars))value=value.split(`{${name}}`).join(String(replacement));return value};
const localeTag=()=>currentLang==="en"?"en-US":"tr-TR";
const csrf=()=>decodeURIComponent((document.cookie.split("; ").find(x=>x.startsWith("NSX.CariTakip.Csrf="))||"=").split("=")[1]||"");
const mutationId=()=>crypto.randomUUID?.()||`${Date.now()}-${Math.random()}`;
const esc=value=>String(value??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
const payload=entity=>entity?.payload||{};
const amount=value=>`${new Intl.NumberFormat(localeTag(),{minimumFractionDigits:2,maximumFractionDigits:2}).format(Number(value||0))} TRY`;
const financeAmount=value=>`${new Intl.NumberFormat(localeTag(),{minimumFractionDigits:2,maximumFractionDigits:2}).format(Number(value||0))} ${currentLang==="en"?"USD":"TL"}`;
function setFinanceAmount(id,value){const node=$(id),text=financeAmount(value);node.textContent=text;node.classList.toggle("finance-long",text.length>=15);node.classList.toggle("finance-xlong",text.length>=18)}
const initials=name=>(String(name||"?").trim().split(/\s+/).slice(0,2).map(x=>x[0]||"").join("")||"?").toUpperCase();
const dateText=value=>{const d=new Date(value);return Number.isNaN(d.getTime())?"-":d.toLocaleDateString(localeTag(),{day:"2-digit",month:"short",year:"numeric"})};
function toast(message){$("toast").textContent=message;$("toast").classList.add("show");clearTimeout(toast.timer);toast.timer=setTimeout(()=>$("toast").classList.remove("show"),2400)}
function applyLanguage(lang,{persist=true}={}){
  currentLang=lang==="en"?"en":"tr";
  if(persist)try{localStorage.setItem("nsx-cari-lang",currentLang)}catch{}
  document.documentElement.lang=currentLang;
  document.querySelectorAll("[data-i18n]").forEach(node=>node.textContent=t(node.dataset.i18n));
  document.querySelectorAll("[data-i18n-placeholder]").forEach(node=>node.setAttribute("placeholder",t(node.dataset.i18nPlaceholder)));
  document.querySelectorAll("[data-i18n-aria]").forEach(node=>node.setAttribute("aria-label",t(node.dataset.i18nAria)));
  $("languageCode").textContent=currentLang.toUpperCase();
  document.querySelectorAll("#languageMenu [data-lang]").forEach(node=>node.classList.toggle("active",node.dataset.lang===currentLang));
  if(!state.companyName)$("company").textContent=t("panelLabel");
  if(state.ready){renderAll();if(state.selected)renderDetail()}
  if($("customerDialog")?.open)$("customerDialogTitle").textContent=$("customerForm").elements.id.value?t("editCustomer"):t("newCustomerTitle");
  if($("transactionDialog")?.open){const form=$("transactionForm"),type=form.elements.type.value;$("transactionDialogTitle").textContent=`${t(type==="debt"?"debt":"collection")} ${t(form.elements.id.value?"transactionEdit":"transactionAdd")}`}
  if($("customerPickerDialog")?.open&&state.pickerType){
    $("pickerTitle").textContent=t(state.pickerType==="debt"?"debtCustomerPick":"collectionCustomerPick");
    const input=$("pickerCustomerSearch");if(input){input.placeholder=t("searchCustomer");input.dispatchEvent(new Event("input"))}
  }
}
function closeLanguageMenu(){$("languageMenu").classList.add("hidden");$("languageButton").setAttribute("aria-expanded","false")}

document.addEventListener("gesturestart",event=>event.preventDefault(),{passive:false});
document.addEventListener("gesturechange",event=>event.preventDefault(),{passive:false});
document.addEventListener("touchstart",event=>{if(event.touches.length>1)event.preventDefault()},{passive:false});

async function request(path,options={}){
  if(isDemo)return demoRequest(path,options);
  const headers={"Accept":"application/json",...(options.body?{"Content-Type":"application/json","X-NSX-CSRF":csrf()}:{})};
  const controller=new AbortController(),timeout=setTimeout(()=>controller.abort(),20000);
  try{
    const response=await fetch(api+path,{credentials:"same-origin",cache:"no-store",...options,signal:controller.signal,headers:{...headers,...options.headers}});
    const data=await response.json().catch(()=>({}));
    if(!response.ok){const error=new Error(response.status===401?"SESSION":data.message||t("operationFailed"));error.status=response.status;error.data=data;throw error}
    return data;
  }catch(error){if(!error.status||error.status===401||error.status>=500||error.status===429)showConnectionError(error);throw error}
  finally{clearTimeout(timeout)}
}
function showConnectionError(error){
  if(error.status===401){state.ready=false;$("app").classList.add("hidden");$("bottomNav").classList.add("hidden");$("loginMessage").classList.remove("hidden");$("connectionStatus").classList.add("hidden");document.querySelectorAll("dialog[open]").forEach(dialog=>dialog.close());return}
  $("loginMessage").classList.add("hidden");$("connectionMessage").textContent=error.status?`${error.message} ${t("sessionRetry")}`:t("internetWaiting");$("connectionStatus").classList.remove("hidden")
}

function seedDemo(){
  const now=new Date();
  const days=n=>new Date(now.getTime()-n*86400000).toISOString();
  state.companyName="NSX Demo İşletme";
  state.companies=[{entityId:"demo-company",payload:{name:"NSX Demo İşletme"}}];
  state.customers=[
    {entityId:"c1",payload:{companyId:"demo-company",name:"ABC Ltd. Şti.",barcodeNo:"CR-0001",phone:"0532 111 22 33",email:"info@abc.com"},version:1,cursor:1,debtTotal:2500,collectionTotal:2500,balance:0},
    {entityId:"c2",payload:{companyId:"demo-company",name:"Mehmet Yılmaz",barcodeNo:"CR-0002",phone:"0533 222 33 44"},version:1,cursor:2,debtTotal:1750,collectionTotal:0,balance:1750},
    {entityId:"c3",payload:{companyId:"demo-company",name:"Kaya Ticaret",barcodeNo:"CR-0003",phone:"0544 333 44 55"},version:1,cursor:3,debtTotal:3200,collectionTotal:3200,balance:0},
    {entityId:"c4",payload:{companyId:"demo-company",name:"Atlas Bilişim",barcodeNo:"CR-0004",phone:"0555 444 55 66"},version:1,cursor:4,debtTotal:400,collectionTotal:600,balance:-200}
  ];
  state.demoTransactions=[
    {entityId:"t1",entityType:"collection",payload:{customerId:"c1",amount:2500,transactionDateUtc:days(0),description:"Havale tahsilatı"},version:1,cursor:10},
    {entityId:"t2",entityType:"debt",payload:{customerId:"c2",amount:1750,transactionDateUtc:days(1),description:"Yeni borç kaydı"},version:1,cursor:11},
    {entityId:"t3",entityType:"collection",payload:{customerId:"c3",amount:3200,transactionDateUtc:days(2),description:"Toplu tahsilat"},version:1,cursor:12},
    {entityId:"t4",entityType:"debt",payload:{customerId:"c4",amount:400,transactionDateUtc:days(3),description:"Hizmet kaydı"},version:1,cursor:13},
    {entityId:"t5",entityType:"debt",payload:{customerId:"c1",amount:2500,transactionDateUtc:days(4),description:"Satış kaydı"},version:1,cursor:14},
    {entityId:"t6",entityType:"debt",payload:{customerId:"c3",amount:3200,transactionDateUtc:days(5),description:"Cari borç"},version:1,cursor:15},
    {entityId:"t7",entityType:"collection",payload:{customerId:"c4",amount:600,transactionDateUtc:days(6),description:"Fazla ödeme"},version:1,cursor:16}
  ];
  state.recentTransactions=[...state.demoTransactions];state.cursor=16;state.ready=true;
}
function recalcDemo(){
  state.customers.forEach(c=>{const tx=state.demoTransactions.filter(t=>payload(t).customerId===c.entityId);c.debtTotal=tx.filter(t=>t.entityType==="debt").reduce((s,t)=>s+Number(payload(t).amount||0),0);c.collectionTotal=tx.filter(t=>t.entityType==="collection").reduce((s,t)=>s+Number(payload(t).amount||0),0);c.balance=c.debtTotal-c.collectionTotal});
  state.recentTransactions=[...state.demoTransactions].sort((a,b)=>new Date(payload(b).transactionDateUtc)-new Date(payload(a).transactionDateUtc));state.cursor++
}
async function demoRequest(path,options={}){
  const method=(options.method||"GET").toUpperCase(),clean=path.split("?")[0],body=options.body?JSON.parse(options.body):{};
  if(clean==="/mobile/session/bootstrap")return{companyName:state.companyName,status:{cursor:state.cursor},customers:state.customers,companies:state.companies,recentTransactions:state.recentTransactions};
  if(clean==="/mobile/companies")return state.companies;
  if(clean==="/mobile/status")return{cursor:state.cursor};
  if(clean==="/mobile/transactions/recent")return state.recentTransactions;
  if(clean==="/mobile/customers"&&method==="GET"){const q=new URLSearchParams(path.split("?")[1]||"").get("search")?.toLocaleLowerCase("tr")||"";return state.customers.filter(c=>!q||`${payload(c).name} ${payload(c).phone} ${payload(c).barcodeNo}`.toLocaleLowerCase("tr").includes(q))}
  const txMatch=clean.match(/^\/mobile\/customers\/([^/]+)\/transactions$/);if(txMatch)return state.demoTransactions.filter(t=>payload(t).customerId===decodeURIComponent(txMatch[1])).sort((a,b)=>new Date(payload(b).transactionDateUtc)-new Date(payload(a).transactionDateUtc));
  if(clean==="/mobile/customers"&&method==="POST"){const id="c"+Date.now();state.customers.push({entityId:id,payload:{...body,barcodeNo:`CR-${String(state.customers.length+1).padStart(4,"0")}`},version:1,cursor:++state.cursor,debtTotal:0,collectionTotal:0,balance:0});return{cursor:state.cursor}}
  const customerMatch=clean.match(/^\/mobile\/customers\/([^/]+)$/);if(customerMatch){const id=decodeURIComponent(customerMatch[1]),c=state.customers.find(x=>x.entityId===id);if(method==="PUT"&&c){c.payload={...c.payload,...body};c.version++;c.cursor=++state.cursor;return{cursor:state.cursor}}if(method==="DELETE"){state.customers=state.customers.filter(x=>x.entityId!==id);return{cursor:++state.cursor}}}
  const entityMatch=clean.match(/^\/mobile\/(debt|collection)s(?:\/([^/]+))?$/);if(entityMatch){const type=entityMatch[1],id=entityMatch[2];if(method==="POST"){state.demoTransactions.push({entityId:"t"+Date.now(),entityType:type,payload:{...body},version:1,cursor:++state.cursor});recalcDemo();return{cursor:state.cursor}}const tx=state.demoTransactions.find(x=>x.entityId===id);if(method==="PUT"&&tx){tx.payload={...tx.payload,...body};tx.version++;tx.cursor=++state.cursor;recalcDemo();return{cursor:state.cursor}}if(method==="DELETE"){state.demoTransactions=state.demoTransactions.filter(x=>x.entityId!==id);recalcDemo();return{cursor:state.cursor}}}
  if(clean==="/mobile/session/revoke")return{success:true};
  return{};
}

async function bootstrapSession(){
  if(isDemo&&state.customers.length===0)seedDemo();
  const data=await request("/mobile/session/bootstrap");
  state.companyName=data.companyName||"NSX Cari Takip";$("company").textContent=state.companyName;$("settingsCompany").textContent=state.companyName;
  $("loginMessage").classList.add("hidden");$("app").classList.remove("hidden");$("bottomNav").classList.remove("hidden");
  state.customers=data.customers||[];state.companies=data.companies||[];state.cursor=Number(data.status?.cursor||0);state.recentTransactions=data.recentTransactions||[];state.selected=null;state.ready=true;
  if(!state.recentTransactions.length)state.recentTransactions=await request("/mobile/transactions/recent?take=30").catch(()=>[]);
  renderAll();
  if(new URLSearchParams(location.search).has("login")&&!isDemo)history.replaceState(null,"","/CaritakipCloud");
}
function start(){
  if(!isDemo&&"serviceWorker"in navigator)navigator.serviceWorker.register("/caritakip/sw.js").catch(error=>console.warn("Mobil çevrimdışı bileşen yüklenemedi.",error));
  startPolling();pollChanges()
}
function totals(){return state.customers.reduce((a,c)=>{a.debt+=Number(c.debtTotal||0);a.collection+=Number(c.collectionTotal||0);a.balance+=Number(c.balance||0);return a},{debt:0,collection:0,balance:0})}
function renderAll(){renderHome();renderCustomers();renderMovements();renderSettings()}
function renderHome(){
  const t=totals();setFinanceAmount("totalReceivable",t.debt);setFinanceAmount("totalCollected",t.collection);setFinanceAmount("remainingBalance",t.balance);
  const rows=state.recentTransactions.slice(0,3);$("homeRecent").innerHTML=rows.length?rows.map((tx,i)=>recentRow(tx,i)).join(""):`<div class="detail-panel empty">${esc(t("noAccountMovement"))}</div>`
}
function recentRow(tx,index=0){const p=payload(tx),customer=state.customers.find(c=>c.entityId===p.customerId),name=payload(customer).name||t("customer"),debt=tx.entityType==="debt";return `<div class="recent-row"><span class="recent-main"><b>${esc(name)}</b><small>${esc(t(debt?"debt":"collection"))}${p.description?" · "+esc(p.description):""}</small></span><span class="recent-amount${debt?" debt":""}"><strong>${debt?"-":"+"}${esc(amount(p.amount))}</strong><small>${esc(dateText(p.transactionDateUtc))}</small></span></div>`}
function movementRow(tx){const p=payload(tx),customer=state.customers.find(c=>c.entityId===p.customerId),name=payload(customer).name||t("customer"),debt=tx.entityType==="debt";return `<div class="movement-item" data-tx="${esc(tx.entityId)}"><span class="movement-kind${debt?" debt":""}"><svg><use href="${debt?"#i-down":"#i-up"}"/></svg></span><span class="movement-copy"><b>${esc(name)}</b><small>${esc(t(debt?"debt":"collection"))}${p.description?" · "+esc(p.description):""}</small></span><span class="movement-side${debt?" debt":""}"><strong>${debt?"-":"+"}${esc(amount(p.amount))}</strong><small>${esc(dateText(p.transactionDateUtc))}</small></span></div>`}
function renderMovements(){$("movementList").innerHTML=state.recentTransactions.length?state.recentTransactions.map(movementRow).join(""):`<section class="detail-panel empty">${esc(t("noMovement"))}</section>`;$("movementList").querySelectorAll("[data-tx]").forEach(n=>{n.style.cursor="pointer";n.onclick=()=>openTransaction(null,state.recentTransactions.find(x=>x.entityId===n.dataset.tx))})}
function renderSettings(){$("settingsCompany").textContent=state.companyName||"-";$("settingsCustomerCount").textContent=String(state.customers.length);$("settingsMode").textContent=t(isDemo?"localDemo":"live")}
function customerSearchText(c){const p=payload(c);return `${p.name||""} ${p.phone||""} ${p.barcodeNo||""}`.toLocaleLowerCase("tr")}
function recentCustomers(limit=5){
  const byId=new Map(state.customers.map(c=>[String(c.entityId),c])),seen=new Set(),result=[];
  const txs=[...state.recentTransactions].sort((a,b)=>{const ad=new Date(payload(a).transactionDateUtc||0).getTime()||0,bd=new Date(payload(b).transactionDateUtc||0).getTime()||0;return bd-ad||Number(b.cursor||0)-Number(a.cursor||0)});
  for(const tx of txs){const id=String(payload(tx).customerId||"");if(!id||seen.has(id)||!byId.has(id))continue;seen.add(id);result.push(byId.get(id));if(result.length>=limit)return result}
  const remaining=state.customers.filter(c=>!seen.has(String(c.entityId))).sort((a,b)=>Number(b.cursor||0)-Number(a.cursor||0));
  for(const c of remaining){result.push(c);if(result.length>=limit)break}
  return result
}
function renderCustomers(){
  const query=$("search").value?.trim().toLocaleLowerCase("tr")||"";
  const visible=query?state.customers.filter(c=>customerSearchText(c).includes(query)):recentCustomers(5);
  $("customerPageSummary").textContent=query?t("matchingCustomers",{count:visible.length}):t("recentCustomersShown",{count:visible.length});
  $("customers").innerHTML=visible.length?visible.map(c=>{const p=payload(c),active=state.selected?.entityId===c.entityId?" active":"",credit=Number(c.balance)<0?" credit":"";return `<button class="customer${active}" data-id="${esc(c.entityId)}" type="button"><span class="customer-copy"><b>${esc(p.name||t("unnamed"))}</b></span><span class="balance${credit}">${esc(amount(c.balance))}</span></button>`}).join(""):`<section class="detail-panel empty">${esc(t("customerNotFound"))}</section>`;
  document.querySelectorAll(".customer").forEach(n=>n.addEventListener("click",()=>selectCustomer(n.dataset.id)))
}
async function refreshCustomers(){state.customers=await request("/mobile/customers?search=&take=500");if(state.selected)state.selected=state.customers.find(x=>x.entityId===state.selected.entityId)||null;renderCustomers();renderHome();renderSettings()}
async function refreshRecent(take=100){state.recentTransactions=await request(`/mobile/transactions/recent?take=${take}`);renderHome();renderMovements()}
async function selectCustomer(id){state.selected=state.customers.find(x=>x.entityId===id)||null;renderCustomers();if(!state.selected){renderDetail();return}state.transactions=await request(`/mobile/customers/${encodeURIComponent(id)}/transactions`);renderDetail();setTab("customers")}
function renderDetail(){
  if(!state.selected){$("detail").className="detail-panel empty";$("detail").innerHTML=`<p>${esc(t("selectCustomerDetail"))}</p>`;return}
  const c=state.selected,p=payload(c),balance=Number(c.balance||0),balanceClass=balance<0?" credit":"";$("detail").className="detail-panel";$("detail").innerHTML=`<div class="detail-head"><div><h3>${esc(p.name||t("customer"))}</h3><p>${esc(p.phone||"")}${p.email?" · "+esc(p.email):""}</p></div><div class="detail-buttons"><button class="mini-btn" data-action="editCustomer">${esc(t("edit"))}</button><button class="mini-btn danger" data-action="deleteCustomer">${esc(t("delete"))}</button></div></div><div class="detail-action-row"><div class="detail-buttons"><button class="mini-btn debt" data-action="addDebt"><svg><use href="#i-down"/></svg><span>${esc(t("debt"))}</span></button><button class="mini-btn green" data-action="addCollection"><svg><use href="#i-up"/></svg><span>${esc(t("collection"))}</span></button></div><div class="detail-balance${balanceClass}"><span>${esc(t("balance"))}</span><strong>${esc(amount(balance))}</strong></div></div><div class="movement-list">${state.transactions.length?state.transactions.map(movementRow).join(""):`<div class="empty">${esc(t("noCustomerMovement"))}</div>`}</div>`;
  $("detail").querySelector('[data-action="editCustomer"]').onclick=()=>openCustomer(c);$("detail").querySelector('[data-action="deleteCustomer"]').onclick=deleteCustomer;$("detail").querySelector('[data-action="addDebt"]').onclick=()=>openTransaction("debt",null,c);$("detail").querySelector('[data-action="addCollection"]').onclick=()=>openTransaction("collection",null,c);
  $("detail").querySelectorAll("[data-tx]").forEach(n=>{n.style.cursor="pointer";n.onclick=()=>openTransaction(null,state.transactions.find(x=>x.entityId===n.dataset.tx))})
}
function setTab(tab){state.tab=tab;document.querySelectorAll(".tab-page").forEach(p=>p.classList.toggle("active",p.dataset.page===tab));document.querySelectorAll("#bottomNav [data-tab]").forEach(b=>b.classList.toggle("active",b.dataset.tab===tab));if(tab==="movements"&&state.ready)refreshRecent(100).catch(e=>toast(e.message));window.scrollTo({top:0,behavior:"smooth"})}

async function openCustomer(customer=null){
  let companies=state.companies.length?state.companies:null;try{companies=companies||await request("/mobile/companies")}catch(error){toast(error.message);return}if(!companies.length){toast(t("companySyncRequired"));return}
  const form=$("customerForm"),p=customer?payload(customer):{};form.reset();form.elements.id.value=customer?.entityId||"";form.elements.version.value=customer?.version||"";form.elements.companyId.innerHTML=`<option value="">${esc(t("selectCompany"))}</option>`+companies.map(x=>`<option value="${esc(x.entityId)}">${esc(payload(x).name||t("companyFallback"))}</option>`).join("");form.elements.companyId.value=p.companyId||(companies.length===1?companies[0].entityId:"");form.elements.barcodeNo.value=p.barcodeNo||"";["name","phone","email","address","notes"].forEach(k=>form.elements[k].value=p[k]||"");$("customerDialogTitle").textContent=customer?t("editCustomer"):t("newCustomerTitle");$("customerDialog").showModal()
}
function chooseCustomerFor(type){
  if(!state.customers.length){toast(t("addCustomerFirst"));openCustomer();return}
  state.pickerType=type;$("pickerTitle").textContent=t(type==="debt"?"debtCustomerPick":"collectionCustomerPick");
  $("customerPicker").innerHTML=`<div class="search-box" style="margin-bottom:2px"><span>⌕</span><input id="pickerCustomerSearch" type="search" maxlength="120" placeholder="${esc(t("searchCustomer"))}" autocomplete="off"></div><small id="pickerCustomerHint" style="color:#7782a5;padding:0 2px 2px"></small><div id="pickerCustomerResults" class="picker-list"></div>`;
  const renderPicker=(raw="")=>{
    const query=raw.trim().toLocaleLowerCase("tr");
    const visible=query?state.customers.filter(c=>customerSearchText(c).includes(query)):recentCustomers(5);
    $("pickerCustomerHint").textContent=query?t("matchingCustomersShort",{count:visible.length}):t("recentCustomersShort",{count:visible.length});
    $("pickerCustomerResults").innerHTML=visible.length?visible.map(c=>`<button class="picker-customer" data-id="${esc(c.entityId)}" type="button"><span><b>${esc(payload(c).name||t("customer"))}</b></span><strong>${esc(amount(c.balance))}</strong></button>`).join(""):`<div class="detail-panel empty">${esc(t("customerNotFound"))}</div>`;
    $("pickerCustomerResults").querySelectorAll("[data-id]").forEach(n=>n.onclick=()=>{$("customerPickerDialog").close();openTransaction(type,null,state.customers.find(c=>c.entityId===n.dataset.id))})
  };
  renderPicker();
  $("pickerCustomerSearch").addEventListener("input",e=>renderPicker(e.currentTarget.value));
  $("customerPickerDialog").showModal();
  setTimeout(()=>$("pickerCustomerSearch")?.focus(),50)
}
function openTransaction(type,entity=null,customer=null){
  const form=$("transactionForm"),p=entity?payload(entity):{};type=type||entity?.entityType;customer=customer||state.customers.find(c=>c.entityId===p.customerId)||state.selected;if(!customer){chooseCustomerFor(type);return}state.transactionCustomer=customer;form.reset();form.elements.id.value=entity?.entityId||"";form.elements.version.value=entity?.version||"";form.elements.type.value=type;form.elements.amount.value=p.amount||"";form.elements.description.value=p.description||"";form.elements.date.value=(p.transactionDateUtc||new Date().toISOString()).slice(0,10);$("transactionCustomerName").value=payload(customer).name||t("customer");$("transactionDialogTitle").textContent=`${t(type==="debt"?"debt":"collection")} ${t(entity?"transactionEdit":"transactionAdd")}`;$("deleteTransactionButton").classList.toggle("hidden",!entity);$("deleteTransactionButton").dataset.entityId=entity?.entityId||"";$("transactionDialog").showModal()
}

$("customerForm").addEventListener("submit",async event=>{event.preventDefault();const form=event.currentTarget,id=form.elements.id.value,body={clientMutationId:mutationId(),expectedVersion:id?Number(form.elements.version.value):0,companyId:form.elements.companyId.value,name:form.elements.name.value,phone:form.elements.phone.value,email:form.elements.email.value,address:form.elements.address.value,notes:form.elements.notes.value};try{const result=await request("/mobile/customers"+(id?"/"+id:""),{method:id?"PUT":"POST",body:JSON.stringify(body)});state.cursor=Math.max(state.cursor,Number(result.cursor||0));$("customerDialog").close();await refreshCustomers("");await refreshRecent();toast(t("customerSaved"));setTab("customers")}catch(error){toast(error.data?.status==="conflict"?t("recordConflict"):error.message);await refreshCustomers("")}});
$("transactionForm").addEventListener("submit",async event=>{event.preventDefault();const form=event.currentTarget,id=form.elements.id.value,type=form.elements.type.value,customer=state.transactionCustomer;if(!customer){toast(t("customerSelectFailed"));return}const body={clientMutationId:mutationId(),expectedVersion:id?Number(form.elements.version.value):0,customerId:customer.entityId,amount:Number(form.elements.amount.value),transactionDateUtc:new Date(form.elements.date.value+"T12:00:00Z").toISOString(),description:form.elements.description.value};try{const result=await request(`/mobile/${type}s`+(id?"/"+id:""),{method:id?"PUT":"POST",body:JSON.stringify(body)});state.cursor=Math.max(state.cursor,Number(result.cursor||0));$("transactionDialog").close();await refreshCustomers("");await refreshRecent();if(state.selected?.entityId===customer.entityId){state.transactions=await request(`/mobile/customers/${encodeURIComponent(customer.entityId)}/transactions`);renderDetail()}toast(t(type==="debt"?"debtSaved":"collectionSaved"));setTab("home")}catch(error){toast(error.message)}});
async function deleteCustomer(){if(!state.selected||!confirm(t("confirmDeleteCustomer")))return;try{const result=await request("/mobile/customers/"+state.selected.entityId,{method:"DELETE",body:JSON.stringify({clientMutationId:mutationId(),expectedVersion:state.selected.version})});state.cursor=Math.max(state.cursor,Number(result.cursor||0));state.selected=null;await refreshCustomers("");renderDetail();toast(t("customerDeleted"))}catch(error){toast(error.message)}}

async function deleteTransaction(entity){if(!entity||!confirm(t("confirmDeleteMovement")))return;try{const result=await request(`/mobile/${entity.entityType}s/${entity.entityId}`,{method:"DELETE",body:JSON.stringify({clientMutationId:mutationId(),expectedVersion:entity.version})});state.cursor=Math.max(state.cursor,Number(result.cursor||0));$("transactionDialog").close();await refreshCustomers();await refreshRecent(100);if(state.selected){state.transactions=await request(`/mobile/customers/${encodeURIComponent(state.selected.entityId)}/transactions`);renderDetail()}toast(t("movementDeleted"))}catch(error){toast(error.message)}}
async function refreshEverything(){try{await refreshCustomers("");await refreshRecent(100);if(state.selected){state.transactions=await request(`/mobile/customers/${encodeURIComponent(state.selected.entityId)}/transactions`);renderDetail()}$("connectionStatus").classList.add("hidden");toast(t("dataUpdated"))}catch(error){toast(error.message)}}
function startPolling(){if(isDemo)return;setInterval(pollChanges,5000);document.addEventListener("visibilitychange",()=>{if(document.visibilityState==="visible")pollChanges()});window.addEventListener("online",pollChanges);window.addEventListener("pageshow",pollChanges)}
async function pollChanges(){if(pollBusy||document.visibilityState!=="visible")return;if(!navigator.onLine){showConnectionError(new Error("offline"));return}pollBusy=true;try{if(!state.ready){await bootstrapSession();$("connectionStatus").classList.add("hidden");return}const status=await request("/mobile/status"),cursor=Number(status.cursor||0);$("connectionStatus").classList.add("hidden");if(cursor<=state.cursor)return;await refreshEverything();state.cursor=cursor}catch(error){showConnectionError(error)}finally{pollBusy=false}}

$("search").addEventListener("input",()=>{clearTimeout(searchTimer);searchTimer=setTimeout(()=>renderCustomers(),180)});
$("deleteTransactionButton").onclick=()=>{const id=$("deleteTransactionButton").dataset.entityId;deleteTransaction(state.recentTransactions.find(x=>x.entityId===id)||state.transactions.find(x=>x.entityId===id))};
$("addCustomer").onclick=()=>openCustomer();$("retryConnection").onclick=pollChanges;$("refreshMovements").onclick=()=>refreshRecent(100).catch(e=>toast(e.message));$("refreshAll").onclick=refreshEverything;
$("logout").onclick=async()=>{if(isDemo){toast(t("demoLogoutDisabled"));return}try{await request("/mobile/session/revoke",{method:"POST",body:"{}"});location.replace("/CaritakipCloud?login=required")}catch(error){toast(t("logoutFailed"))}};
$("brandHome").onclick=()=>setTab("home");$("profileButton").onclick=()=>setTab("settings");$("viewAllMovements").onclick=()=>setTab("movements");$("balanceShortcut").onclick=()=>setTab("customers");
$("languageButton").onclick=event=>{event.stopPropagation();const menu=$("languageMenu"),open=menu.classList.contains("hidden");menu.classList.toggle("hidden",!open);$("languageButton").setAttribute("aria-expanded",String(open))};
document.querySelectorAll("#languageMenu [data-lang]").forEach(node=>node.onclick=()=>{applyLanguage(node.dataset.lang);closeLanguageMenu()});
document.addEventListener("click",event=>{if(!event.target.closest(".language-control"))closeLanguageMenu()});
document.addEventListener("keydown",event=>{if(event.key==="Escape")closeLanguageMenu()});
document.querySelectorAll("#bottomNav [data-tab]").forEach(n=>n.onclick=()=>setTab(n.dataset.tab));
document.querySelectorAll("[data-quick]").forEach(n=>n.onclick=()=>{const a=n.dataset.quick;if(a==="customers")setTab("customers");else if(a==="new-customer")openCustomer();else if(a==="collection")chooseCustomerFor("collection");else if(a==="debt")chooseCustomerFor("debt")});
document.querySelectorAll("[data-close]").forEach(n=>n.onclick=()=>n.closest("dialog").close());
applyLanguage(currentLang,{persist:false});
start();
