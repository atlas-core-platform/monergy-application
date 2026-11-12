import fs from 'node:fs';
import path from 'node:path';
const root=process.cwd();
const modulePath=process.env.MONERGY_PLAYWRIGHT_MODULE;
if(!modulePath)throw new Error('MONERGY_PLAYWRIGHT_MODULE is required.');
const playwright=await import(modulePath);
const chromium=playwright.chromium??playwright.default?.chromium;
if(!chromium)throw new Error('Playwright Chromium is unavailable.');
const base='http://127.0.0.1:4301';
const privateState=JSON.parse(fs.readFileSync(path.join(root,'.artifacts/cohort-o/private/login-private.json'),'utf8'));
const acceptedState=JSON.parse(fs.readFileSync(path.join(root,'.artifacts/cohort-o/cohort-o-state.json'),'utf8'));
const acceptedNetWorthCalculation=acceptedState.qa.summary.claims.find(claim=>claim.kind==='Calculation')?.calculationId;
const acceptedAmount=id=>acceptedState.qa.summary.claims.find(claim=>claim.id===id)?.text.match(/: ([0-9.]+) /)?.[1];
const acceptedNetWorthAmounts={assets:acceptedAmount('calculation-assets'),liabilities:acceptedAmount('calculation-liabilities'),netWorth:acceptedAmount('calculation-net-worth')};
if(!acceptedNetWorthCalculation||Object.values(acceptedNetWorthAmounts).some(value=>!value))throw new Error('Accepted Cohort O net-worth result is missing.');
const executablePath='C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
if(!fs.existsSync(executablePath))throw new Error('Installed Google Chrome was not found.');
const browser=await chromium.launch({executablePath,headless:true});
const results=[];

async function visible(page,text){await page.getByText(text,{exact:false}).first().waitFor({state:'visible',timeout:30000});}
async function heading(page,name){await page.getByRole('heading',{name,exact:typeof name==='string'}).first().waitFor({state:'visible',timeout:30000});}
async function credentialEntry(page,route='/'){
  await page.goto(base+route,{waitUntil:'domcontentloaded'});
  await page.getByText('Checking available sign-in methods…').or(page.getByRole('heading',{name:'Sign in to Monergy'})).first().waitFor({state:'visible',timeout:30000});
  await heading(page,'Sign in to Monergy');
}
async function submitLogin(page,tenant='T001',password=privateState.Password){
  await page.getByLabel('Organization').fill(tenant);
  await page.getByLabel('Email address').fill(privateState.Email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button',{name:'Sign in',exact:true}).click();
}
async function login(page,route='/',target=/Good (morning|to see you)\./){
  await credentialEntry(page,route);await submitLogin(page);await heading(page,target);
}
async function secretFree(page){
  const body=await page.locator('body').innerText();
  const storage=await page.evaluate(()=>JSON.stringify({local:{...localStorage},session:{...sessionStorage}}));
  if(body.includes(privateState.Password)||storage.includes(privateState.Password)||storage.includes('authenticationContextId'))
    throw new Error('Protected credential appeared in rendered or browser storage state.');
}
async function scenario(name,run){
  const started=Date.now();const context=await browser.newContext({locale:'en-IN',timezoneId:'Asia/Calcutta'});const page=await context.newPage();const errors=[];
  page.on('pageerror',error=>errors.push(error.message));
  try{await run(page);if(errors.length)throw new Error('Unhandled browser error.');await secretFree(page);results.push({name,status:'PASS',durationMs:Date.now()-started});}
  catch(error){const visibleText=(await page.locator('body').innerText().catch(()=>'' )).replaceAll(privateState.Password,'[redacted]').slice(0,900);results.push({name,status:'FAIL',durationMs:Date.now()-started,error:error instanceof Error?error.message:String(error),visible:visibleText});}
  finally{process.stderr.write(`${name}: ${results.at(-1).status}\n`);await context.close();}
}

await scenario('01 password-first method discovery',async page=>{await credentialEntry(page);if(await page.getByLabel('Local access key').isVisible())throw new Error('Local key flashed after discovery.');});
await scenario('02 valid password reaches customer workspace',async page=>{await login(page);});
await scenario('03 invalid password denied',async page=>{await credentialEntry(page);await submitLogin(page,'T001',privateState.Password+'x');await visible(page,'Unable to sign in.');});
await scenario('04 foreign tenant denied',async page=>{await credentialEntry(page);await submitLogin(page,'T002');await visible(page,'Unable to sign in.');});
await scenario('05 cancelled verification revokes late session',async page=>{
  let issued=null;
  page.on('response',async response=>{if(response.url().includes('/persona-credentials/sign-in')&&response.status()===200)issued=await response.json();});
  await page.route('**/product-api/v1/context',async route=>{const response=await route.fetch();await new Promise(resolve=>setTimeout(resolve,2000));await route.fulfill({response});});
  await credentialEntry(page);await submitLogin(page);
  await page.getByRole('button',{name:'Use another sign-in method'}).waitFor({state:'visible',timeout:30000});
  for(let i=0;i<50&&!issued;i++)await page.waitForTimeout(100);
  if(!issued)throw new Error('No session was issued before verification cancellation.');
  await page.getByRole('button',{name:'Use another sign-in method'}).click();
  await page.waitForTimeout(2600);
  const status=await page.evaluate(async({base,tenant,session})=>(await fetch(base+'/identity-api/local/v1/me/customer-profile',{headers:{'X-Monergy-Tenant':tenant,'X-Monergy-Session':session}})).status,{base,tenant:'T001',session:issued.authenticationContextId});
  if(status!==401)throw new Error('Late issued session was not revoked.');
});
await scenario('06 sign-out ends active session',async page=>{await login(page);await page.getByRole('button',{name:'Sign out'}).click();await heading(page,'Sign in to Monergy');});
await scenario('07 My Profile retained fields',async page=>{await login(page,'/profile','My Profile');if(await page.getByLabel('Your name').inputValue()!=='Reference Customer Cohort O')throw new Error('Retained display name was not loaded.');if(await page.getByLabel('Mobile number').inputValue()!=='+919876543210')throw new Error('Retained mobile number was not loaded.');if(await page.getByLabel('Date of birth').inputValue()!=='1992-04-09')throw new Error('Retained birth date was not loaded.');});
await scenario('08 masked PAN only',async page=>{await login(page,'/profile','My Profile');await page.getByRole('button',{name:'Manage PAN'}).click();await visible(page,'******234F');});
await scenario('09 Basic KYC accepted state',async page=>{await login(page,'/kyc','Basic KYC');await visible(page,'Manual review accepted');});
await scenario('10 KYC nonclaim boundary',async page=>{await login(page,'/kyc','Basic KYC');await visible(page,'Manual acceptance is not government, Aadhaar, bank or regulated-provider verification.');});
await scenario('11 Document Vault opens',async page=>{await login(page,'/evidence','Keep your financial documents together.');await visible(page,'Documents');});
await scenario('12 replacement version visible',async page=>{await login(page,'/evidence','Keep your financial documents together.');await visible(page,'cohort-o-corpus-001-replacement.csv');});
await scenario('13 later-page document searchable',async page=>{await login(page,'/search','Find information already in your Monergy workspace.');await page.getByLabel('Search query').fill('cohort-o-corpus-205.csv');await page.getByRole('button',{name:'Search',exact:true}).click();await visible(page,'cohort-o-corpus-205.csv');});
await scenario('14 Assets dashboard',async page=>{await login(page);await heading(page,'Assets');await visible(page,'Total assets in this review');});
await scenario('15 Liabilities dashboard',async page=>{await login(page);await heading(page,'Liabilities');await visible(page,'Total liabilities in this review');});
await scenario('16 Net Worth exact owner result',async page=>{await login(page,'/net-worth?calculation='+encodeURIComponent(acceptedNetWorthCalculation),'Net Worth');const result=page.locator('section[aria-label="Saved net worth calculation"]');await result.waitFor({state:'visible',timeout:30000});const rendered=(await result.innerText()).replaceAll(',','');for(const value of Object.values(acceptedNetWorthAmounts))if(!rendered.includes('INR '+value))throw new Error('Exact accepted Net Worth owner result was not rendered.');});
await scenario('17 Net Worth preserves Financial Rules state',async page=>{await login(page,'/net-worth?calculation='+encodeURIComponent(acceptedNetWorthCalculation),'Net Worth');await page.locator('section[aria-label="Saved net worth calculation"]').waitFor({state:'visible',timeout:30000});await page.getByText('Calculation details',{exact:true}).click();await visible(page,'Rule: monergy.net-worth');await visible(page,'Calculation: '+acceptedNetWorthCalculation);});
await scenario('18 Monthly cashflow opens',async page=>{await login(page,'/monthly-cashflow','Income and Expenses');await visible(page,'Monthly balance');});
await scenario('19 Monthly comparison retained',async page=>{await login(page,'/monthly-cashflow','Income and Expenses');await visible(page,'Compare');});
await scenario('20 current financial Search',async page=>{await login(page,'/search','Find information already in your Monergy workspace.');await page.getByLabel('Search query').fill('investment');await page.locator('label.ant-radio-button-wrapper').filter({hasText:'Financial information'}).click();await page.getByRole('button',{name:'Search',exact:true}).click();await visible(page,'Current value stated on statement');});
await scenario('21 Financial Q&A history',async page=>{await login(page,'/financial-qa','Financial Q&A and summary');await visible(page,'What investment source is recorded?');});
await scenario('22 exact Q&A source links',async page=>{await login(page,'/financial-qa','Financial Q&A and summary');await visible(page,'Open source document version');await visible(page,'Open exact calculation');});
await scenario('23 six-claim summary rendered',async page=>{await login(page,'/financial-qa','Financial Q&A and summary');for(const text of ['total assets','total liabilities','net worth','monthly income','monthly expenses','monthly income minus expenses'])await visible(page,text);});
await scenario('24 sessions and agreements',async page=>{await login(page,'/settings/sessions','Devices & sessions');await visible(page,'Current session');await page.getByRole('link',{name:'Account agreements',exact:true}).click();await heading(page,'Account agreements');await visible(page,'Current agreements recorded');});
await scenario('25 customer denied Organization Settings',async page=>{await credentialEntry(page,'/access');await submitLogin(page);await visible(page,'Organization Administrator access is required.');});
await scenario('26 recovery control is available',async page=>{await credentialEntry(page);await page.getByRole('button',{name:'Forgot password'}).click();await heading(page,'Reset your password');});
await scenario('27 password lockout rate limit',async page=>{
  await page.goto(base+'/');
  const outcome=await page.evaluate(async({email,password})=>{
    const headers={'Content-Type':'application/json','X-Monergy-Client-Id':'cohort-o-lockout','X-Monergy-Client-Label':'Cohort O lockout','X-Monergy-Client-Type':'browser'};
    const bad=[];for(let i=0;i<6;i++)bad.push((await fetch('/identity-api/local/v1/persona-credentials/sign-in',{method:'POST',headers,body:JSON.stringify({tenantId:'T001',email,password:password+'bad'})})).status);
    const locked=(await fetch('/identity-api/local/v1/persona-credentials/sign-in',{method:'POST',headers,body:JSON.stringify({tenantId:'T001',email,password})})).status;
    const unknown=`cohort-o-rate-limit-${Date.now()}@example.test`;
    const limited=await Promise.all(Array.from({length:130},()=>fetch('/identity-api/local/v1/persona-credentials/activation/request',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({tenantId:'T001',email:unknown})}).then(response=>response.status)));
    return {bad,locked,limited};
  },{email:privateState.Email,password:privateState.Password});
  if(outcome.bad.some(status=>status!==401)||outcome.locked!==401)throw new Error('Password lockout was not enforced.');
  if(!outcome.limited.includes(429))throw new Error('Credential endpoint rate limit was not enforced.');
});

const failed=results.filter(value=>value.status==='FAIL');
const evidence={recordedAt:new Date().toISOString(),browser:'Google Chrome',browserVersion:browser.version(),installedChannel:true,executed:results.length,passed:results.length-failed.length,failed:failed.length,blocked:0,scenarios:results};
await browser.close();
fs.writeFileSync(path.join(root,'.artifacts/cohort-o/chrome-evidence.json'),JSON.stringify(evidence,null,2),'utf8');
process.stdout.write(JSON.stringify(evidence,null,2)+'\n');
if(failed.length)process.exitCode=1;
