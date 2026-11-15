namespace Gt5pOnlineServer.Services;

// Single-file /admin Web UI (no dependencies, no build step).
// Served as a static string so `dotnet run` needs nothing extra.
public static class AdminUi
{
    public const string Html = """
<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>GT5P Grim Admin</title>
<style>
body{font-family:Consolas,monospace;background:#111;color:#ddd;margin:0}
header{background:#222;padding:8px 12px;position:sticky;top:0}
header b{color:#8f8}button{margin-left:8px}
nav button{background:#333;color:#ddd;border:1px solid #555;padding:4px 12px;cursor:pointer}
nav button.on{background:#465;color:#fff}
table{border-collapse:collapse;width:100%;font-size:12px}
td,th{border:1px solid #333;padding:3px 6px;text-align:left;vertical-align:top}
tr:nth-child(even){background:#181818}tr:hover{background:#222}
pre{white-space:pre-wrap;word-break:break-all;margin:0;max-height:300px;overflow:auto;background:#0a0a0a;padding:6px}
.ok{color:#8f8}.err{color:#f88}.dim{color:#888}
section{padding:8px 12px}
</style></head><body>
<header><b>GT5P Grim Admin</b> <span id="stat" class="dim"></span>
<button onclick="loadAll()">refresh</button>
<button onclick="clearTraffic()">clear traffic</button>
<label class="dim"><input type="checkbox" id="auto" checked> auto (2s)</label></header>
<nav><button id="t-traffic" class="on" onclick="tab('traffic')">traffic</button><button id="t-users" onclick="tab('users')">users</button><button id="t-rankings" onclick="tab('rankings')">rankings</button><button id="t-rooms" onclick="tab('rooms')">rooms</button></nav>
<section id="s-traffic"><table><thead><tr><th>#</th><th>time</th><th>method</th><th>path</th><th>status</th><th>ms</th><th>reqB</th><th>resB</th></tr></thead><tbody id="b-traffic"></tbody></table><div id="d-traffic"></div></section>
<section id="s-users" hidden><table><thead><tr><th>#</th><th>online_id</th><th>region</th><th>last login</th></tr></thead><tbody id="b-users"></tbody></table></section>
<section id="s-rankings" hidden><table><thead><tr><th>board</th><th>rank</th><th>online_id</th><th>time_ms</th><th>car</th></tr></thead><tbody id="b-rankings"></tbody></table></section>
<section id="s-rooms" hidden><div id="b-rooms"></div></section>
<script>
let cur='traffic',detail={};
function tab(n){cur=n;document.querySelectorAll('nav button').forEach(b=>b.classList.remove('on'));document.getElementById('t-'+n).classList.add('on');['traffic','users','rankings','rooms'].forEach(x=>document.getElementById('s-'+x).hidden=x!==n);loadAll();}
function esc(s){return String(s??'').replace(/&/g,'&amp;').replace(/</g,'&lt;');}
async function jget(p){const r=await fetch(p);return r.json();}
function show(i){const e=detail[i];if(!e)return;document.getElementById('d-traffic').innerHTML='<h4>#'+e.id+' '+esc(e.method)+' '+esc(e.path)+'</h4><b>req headers</b><pre>'+esc(e.reqHeaders)+'</pre><b>req body ('+e.reqBytes+'B)</b><pre>'+esc(e.reqBodyPreview)+'</pre><b>res</b> '+e.status+' '+esc(e.resContentType)+' ('+e.resBytes+'B)';}
async function loadTraffic(){const list=await jget('/admin/api/traffic?n=100');detail={};document.getElementById('b-traffic').innerHTML=list.map(e=>{detail[e.id]=e;const c=e.status>=500?'err':(e.status>=400?'err':'ok');return `<tr onclick="show(${e.id})"><td>${e.id}</td><td class="dim">${esc(e.time)}</td><td>${esc(e.method)}</td><td>${esc(e.path)}</td><td class="${c}">${e.status}</td><td>${e.durationMs.toFixed(1)}</td><td>${e.reqBytes}</td><td>${e.resBytes}</td></tr>`;}).join('');document.getElementById('stat').textContent=list.length+' reqs';}
async function loadUsers(){const l=await jget('/admin/api/users');document.getElementById('b-users').innerHTML=l.map(u=>`<tr><td>${u.userNumber}</td><td>${esc(u.onlineId)}</td><td>${esc(u.region)}</td><td class="dim">${esc(u.lastLogin)}</td></tr>`).join('');}
async function loadRankings(){const l=await jget('/admin/api/rankings');let n=0;document.getElementById('b-rankings').innerHTML=l.map(r=>`<tr><td>${esc(r.boardId)}</td><td>${++n}</td><td>${esc(r.onlineId)}</td><td>${r.timeMs}</td><td>${esc(r.car)}</td></tr>`).join('');}
async function loadRooms(){const l=await jget('/admin/api/rooms');document.getElementById('b-rooms').innerHTML=l.length?l.map(r=>`<h4>event ${esc(r.event_id)} type ${esc(r.online_type)} host=${esc(r.host_user_id??'none')}</h4><pre>${esc(JSON.stringify(r.players,null,1))}</pre>`).join(''):'<p class="dim">no active rooms</p>';}
async function loadAll(){try{if(cur==='traffic')await loadTraffic();else if(cur==='users')await loadUsers();else if(cur==='rankings')await loadRankings();else await loadRooms();}catch(e){}}
async function clearTraffic(){await fetch('/admin/api/traffic/clear',{method:'POST'});loadTraffic();}
setInterval(()=>{if(document.getElementById('auto').checked&&cur==='traffic')loadTraffic();},2000);
loadAll();
</script></body></html>
""";
}
