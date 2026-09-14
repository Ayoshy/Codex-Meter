const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright-core');
const assert=require('node:assert/strict');
const path=require('node:path');
const fs=require('node:fs');
const wallpaper=process.argv[2]||'C:/Program Files (x86)/Steam/steamapps/common/wallpaper_engine/projects/myprojects/gta-vi-wallpaper';
const output=path.resolve(__dirname,'../artifacts');fs.mkdirSync(output,{recursive:true});
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:process.env.CHROMIUM_EXECUTABLE});
 try{
  const page=await browser.newPage({viewport:{width:5120,height:1440}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto('file:///'+wallpaper+'/index.html');
  await page.evaluate(()=>window.wallpaperPropertyListener.applyUserProperties({theme:{value:'night'},positionx:{value:85},scale:{value:104},screenlayout:{value:'auto'}}));
  await page.waitForFunction(()=>document.querySelector('#meter-primary').textContent!=='—');
  await page.waitForFunction(()=>document.querySelector('#sensor-cpu').textContent!=='—');
  const live=await page.evaluate(async()=>await(await fetch('http://127.0.0.1:19188/state',{headers:{'X-Codex-Meter-Token':window.CODEX_METER_CONNECTION.token}})).json());
  const main=live.snapshot.limits.find(l=>l.id==='codex')||live.snapshot.limits[0];
  assert.equal(await page.locator('#meter-primary').textContent(),Math.round(100-main.primary.usedPercent)+'%');
  assert.equal(await page.locator('#meter-secondary-card').isVisible(),!!main.secondary);
  async function bounds(width,height){
   for(const selector of ['#composition','#conrad','#codex-meter']){
    const b=await page.locator(selector).boundingBox();
    assert(b.x>=(width/height>2.7?width/2:0)&&b.x+b.width<=width+1&&b.y>=0&&b.y+b.height<=height+1,selector+JSON.stringify({width,height,b}));
   }
  }
  await bounds(5120,1440);
  await page.screenshot({path:path.join(output,'wallpaper-dashboard.png')});
  await page.locator('#meter-limits-button').click();
  assert(await page.locator('#meter-limits').isVisible());
  assert.equal(await page.locator('.meter-limit-row').count(),live.snapshot.limits.length);
  await page.locator('#meter-models-button').click();
  assert.equal(await page.locator('#meter-limits').isVisible(),false);
  assert.equal(await page.locator('.meter-model-row').count(),live.snapshot.models.length);
  await bounds(5120,1440);
  await page.screenshot({path:path.join(output,'wallpaper-models.png')});
  await page.locator('#sensor-details-button').click();
  assert.equal(await page.locator('#meter-models').isVisible(),false);
  await page.locator('#meter-limits-button').click();
  assert.equal(await page.locator('#sensor-details').isVisible(),false);
  const refreshed=page.waitForResponse(r=>r.url().endsWith('/refresh')&&r.request().method()==='POST',{timeout:95000});
  await page.locator('#meter-refresh').click();
  const refreshedState=await(await refreshed).json();
  assert(!refreshedState.error);assert(Date.parse(refreshedState.snapshot.fetchedAt)>Date.parse(live.snapshot.fetchedAt));
  console.log('PASS: live quota, token/model data, and real read-only refresh');
  // Empty data and disconnection must not become fictitious zero/100% quotas.
  await page.route('**/state',async route=>{
   if(route.request().url().includes(':19188/'))return route.fulfill({status:200,contentType:'application/json',body:JSON.stringify({snapshot:null,refreshing:false,error:null,refreshIntervalMinutes:15})});
   await route.continue();
  });
  await page.evaluate(()=>{window.wallpaperPropertyListener.setPaused(true);window.wallpaperPropertyListener.setPaused(false);});
  await page.waitForFunction(()=>document.querySelector('#meter-primary').textContent==='—');
  await page.unroute('**/state');
  await page.route('**/state',route=>route.request().url().includes(':19188/')?route.abort():route.continue());
  await page.evaluate(()=>{window.wallpaperPropertyListener.setPaused(true);window.wallpaperPropertyListener.setPaused(false);});
  await page.waitForFunction(()=>document.querySelector('#meter-status').textContent==='CODEX METER HORS LIGNE');
  assert(await page.locator('#meter-refresh').isDisabled());
  assert.notEqual(await page.locator('#sensor-cpu').textContent(),'—');
  await page.unroute('**/state');
  await page.waitForFunction(()=>document.querySelector('#meter-primary').textContent!=='—');
  let calls=0;page.on('request',r=>{if(r.url().includes(':19188/'))calls++;});
  await page.evaluate(()=>window.wallpaperPropertyListener.setPaused(true));
  const before=calls;await page.waitForTimeout(2000);assert.equal(calls,before);
  await page.evaluate(()=>window.wallpaperPropertyListener.setPaused(false));
  await page.waitForTimeout(500);assert(calls>before);
  for(const[width,height]of[[3840,1080],[2560,1440],[1920,1080]]){
   await page.setViewportSize({width,height});
   for(const selector of ['#meter-models-button','#sensor-details-button','#sensor-cooling-button','#meter-limits-button']){
    await page.locator(selector).click();await bounds(width,height);
   }
  }
  assert.deepEqual(errors,[]);
  console.log('PASS: secondary-screen bounds, drawer exclusivity, unavailable/offline data, independent Conrad connection, pause/resume, responsive sizes; no JS errors');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
