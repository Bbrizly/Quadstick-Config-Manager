const REPO='Bbrizly/Quadstick-Config-Manager';
const TAGS=['beginner','low-fatigue','competitive','one-mode','limited-sip','limited-puff'];
let catalog={games:[],devices:[],profiles:[]};
const $=id=>document.getElementById(id);
const label=s=>s.replaceAll('-',' ').replace(/\b\w/g,c=>c.toUpperCase());
const byId=(items,id)=>items.find(x=>x.id===id);

function switchView(name){
  document.querySelectorAll('.view').forEach(v=>v.hidden=v.id!==name);
  document.querySelectorAll('[data-view]').forEach(b=>b.classList.toggle('active',b.dataset.view===name));
  history.replaceState(null,'',`#${name}`); window.scrollTo({top:0,behavior:'smooth'});
}
document.addEventListener('click',e=>{const go=e.target.closest('[data-view],[data-go]');if(go&&go.dataset.view){switchView(go.dataset.view)}else if(go&&go.dataset.go){switchView(go.dataset.go)}});

async function load(){
  try{
    const r=await fetch('./data/index.json',{cache:'no-cache'}); if(!r.ok)throw new Error(`${r.status}`); catalog=await r.json();
    setupFilters(); setupForm(); render(); $('catalogStatus').textContent=`${catalog.games.length} games · ${catalog.devices.length} devices · ${catalog.profiles.length} community profiles`;
  }catch(e){$('catalogStatus').textContent='Registry could not be loaded. Try again later.';console.error(e)}
}
function setupFilters(){
  const platforms=[...new Set(catalog.games.flatMap(g=>g.platforms))].sort();
  $('platform').innerHTML='<option value="">All platforms</option>'+platforms.map(p=>`<option value="${p}">${label(p)}</option>`).join('');
  $('device').innerHTML='<option value="">All devices</option>'+catalog.devices.map(d=>`<option value="${d.id}">${d.name}</option>`).join('');
  ['search','platform','device'].forEach(id=>$ (id).addEventListener('input',render));
}
function render(){
  const q=$('search').value.trim().toLowerCase(),p=$('platform').value,d=$('device').value;
  const profiles=catalog.profiles.filter(x=>{const game=byId(catalog.games,x.gameId);return(!q||`${game?.name??''} ${x.variant} ${x.contributor}`.toLowerCase().includes(q))&&(!p||x.platform===p)&&(!d||x.deviceId===d)});
  $('results').innerHTML=profiles.map(profileCard).join('');
  $('empty').hidden=profiles.length>0;
  $('requestProfile').onclick=()=>requestProfile(p,d,q);
}
function profileCard(x){
  const game=byId(catalog.games,x.gameId),device=byId(catalog.devices,x.deviceId); const actions=new Map((game?.actions??[]).map(a=>[a.id,a.label])); const inputs=new Map((device?.inputs??[]).map(i=>[i.id,i.label]));
  const rows=x.bindings.slice(0,8).map(b=>`<tr><td>${esc(actions.get(b.actionId)||label(b.actionId))}</td><td>${esc(inputs.get(b.inputId)||label(b.inputId))}</td></tr>`).join('');
  return `<article class="card"><div class="cardhead"><div><h2>${esc(game?.name||x.gameId)}</h2><p class="meta">${esc(label(x.platform))} · ${esc(device?.name||x.deviceId)} · by ${esc(x.contributor)}</p></div><span class="badge">${esc(label(x.variant))}</span></div><table class="mapping" aria-label="Action mappings"><tbody>${rows}</tbody></table><div class="actions"><a class="primary" href="qcm://profile/${encodeURIComponent(x.id)}">Open in QCM</a><a class="secondary" href="${sheetUrl(x.source.sheetId)}" target="_blank" rel="noreferrer">View source</a></div></article>`;
}
function requestProfile(platform,device,query){
  const game=catalog.games.find(g=>g.name.toLowerCase()===query)||catalog.games.find(g=>g.name.toLowerCase().includes(query));
  const title=`[REQUEST] ${game?.id||'game'} ${platform||'platform'} ${device||'device'}`;
  const body='Request a missing adaptive-control profile. Do not include private or medical information.\n\nGame: '+(game?.id||query||'')+'\nPlatform: '+platform+'\nDevice: '+device;
  window.open(`https://github.com/${REPO}/issues/new?title=${encodeURIComponent(title)}&body=${encodeURIComponent(body)}`,'_blank','noopener');
}

function setupForm(){
  $('formGame').innerHTML=catalog.games.map(g=>`<option value="${g.id}">${g.name}</option>`).join('');
  $('formDevice').innerHTML=catalog.devices.map(d=>`<option value="${d.id}">${d.name}</option>`).join('');
  $('tags').innerHTML=TAGS.map(t=>`<label class="chip"><input type="checkbox" value="${t}"><span>${label(t)}</span></label>`).join('');
  $('formGame').addEventListener('change',refreshForm); $('formDevice').addEventListener('change',refreshBindings); refreshForm();
  $('profileForm').addEventListener('submit',submitProfile);
}
function refreshForm(){
  const game=byId(catalog.games,$('formGame').value); $('formPlatform').innerHTML=(game?.platforms??[]).map(p=>`<option value="${p}">${label(p)}</option>`).join(''); refreshBindings();
}
function refreshBindings(){
  const game=byId(catalog.games,$('formGame').value),device=byId(catalog.devices,$('formDevice').value); if(!game||!device)return;
  const opts='<option value="">Not mapped</option>'+device.inputs.map(i=>`<option value="${i.id}">${i.label}</option>`).join('');
  $('bindings').innerHTML=game.actions.map(a=>`<div class="binding" data-action="${a.id}"><span class="action">${esc(a.label)}</span><select aria-label="Input for ${esc(a.label)}">${opts}</select><select class="behavior" aria-label="Behavior for ${esc(a.label)}"><option value="normal">Normal</option><option value="tap">Tap</option><option value="hold">Hold</option><option value="toggle">Toggle</option><option value="repeat">Repeat</option></select></div>`).join('');
}
function submitProfile(e){
  e.preventDefault(); $('formError').textContent='';
  const id=extractSheetId($('sheet').value); if(!id){$('formError').textContent='That does not look like a public Google Sheet URL or ID.';return}
  const bindings=[...document.querySelectorAll('.binding')].flatMap(row=>{const input=row.querySelector('select').value;if(!input)return[];return[{actionId:row.dataset.action,inputId:input,behavior:row.querySelector('.behavior').value}]});
  if(!bindings.length){$('formError').textContent='Map at least one game action.';return}
  const csv=$('csvName').value.trim(); if(csv&&!/^[A-Za-z0-9][A-Za-z0-9._ -]{0,99}\.csv$/.test(csv)){$('formError').textContent='CSV name must end in .csv and contain only normal filename characters.';return}
  const payload={gameId:$('formGame').value,platform:$('formPlatform').value,deviceId:$('formDevice').value,variant:$('formVariant').value,source:{sheetId:id,...(csv?{csvName:csv}:{})},bindings,tags:[...$('tags').querySelectorAll('input:checked')].map(x=>x.value)};
  const encoded=base64url(JSON.stringify(payload));
  const title=`[PROFILE] ${payload.gameId} ${payload.platform} ${payload.deviceId} ${payload.variant}`;
  const body=`Structured profile submission generated by Adaptive Profiles. Do not edit the payload below.\n\n<!-- PROFILE_PAYLOAD\n${encoded}\nPROFILE_PAYLOAD -->`;
  window.open(`https://github.com/${REPO}/issues/new?title=${encodeURIComponent(title)}&body=${encodeURIComponent(body)}`,'_blank','noopener');
}
function extractSheetId(value){const v=value.trim();const m=v.match(/\/spreadsheets\/d\/([A-Za-z0-9_-]{20,200})/);if(m)return m[1];return /^[A-Za-z0-9_-]{20,200}$/.test(v)?v:''}
function sheetUrl(id){return`https://docs.google.com/spreadsheets/d/${encodeURIComponent(id)}/edit`}
function base64url(text){const bytes=new TextEncoder().encode(text);let binary='';for(const b of bytes)binary+=String.fromCharCode(b);return btoa(binary).replaceAll('+','-').replaceAll('/','_').replace(/=+$/,'')}
function esc(s){return String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))}

if(location.hash==='#contribute')switchView('contribute');
load();
