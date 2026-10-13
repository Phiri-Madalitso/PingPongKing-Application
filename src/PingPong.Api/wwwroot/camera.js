/* Kamera-assistert scoring.
 *
 * Kameraet står på LANGSIDEN av bordet og ser hele bordet fra kant til kant (se oppsettguiden i appen).
 * I bildet er x langs bordet og y opp/ned. Da gjelder:
 *   - et sprett = ballens y når et lokalt maksimum (lengst ned) nær bordflaten
 *   - hvilken side = om x er til venstre eller høyre for nettet (midt mellom bordendene)
 *   - poeng = rallyet er over (ballen borte / ingen nye sprett), og den siden ballen sprettet på SIST
 *     har tapt poenget -> motstanderen scorer. Dette dekker: ikke returnert, i nettet, for lang, ute.
 * Alt er sannsynlighetsbasert på et vanlig kamera, så appen kan foreslå og la deg avvise.
 * Logikken (RallyEngine, detectBall) er ren og kan testes uten nettleser.
 */
(() => {
'use strict';

class RallyEngine {
  /** cfg: { leftX, rightX, surfaceY, ...overstyringer } – alle mål i bildets piksler */
  constructor(cfg) {
    this.c = Object.assign({ lostMs: 1200, idleMs: 2500, minGapMs: 80, maxSampleGapMs: 120, minDy: 1, bandFrac: 0.12 }, cfg);
    this.c.netX = (this.c.leftX + this.c.rightX) / 2;
    this.c.band = (this.c.rightX - this.c.leftX) * this.c.bandFrac;
    this.reset();
  }
  reset() { this.pts = []; this.track = []; this.bounces = []; this.lastSeen = 0; }

  /** Kalles hver frame. pt = {x,y} eller null. Returnerer liste med hendelser. */
  push(t, pt) {
    const c = this.c, events = [];
    if (pt) {
      const prev = this.pts[this.pts.length - 1];
      if (prev && t - prev.t > c.maxSampleGapMs) this.pts = [];          // for langt hull: ikke tolk som sammenhengende bane
      this.pts.push({ t, x: pt.x, y: pt.y });
      if (this.pts.length > 3) this.pts.shift();
      this.track.push({ t, x: pt.x, y: pt.y });
      if (this.track.length > 600) this.track.shift();
      this.lastSeen = t;

      if (this.pts.length === 3) {
        const [p0, p1, p2] = this.pts;
        const lastB = this.bounces[this.bounces.length - 1];
        const isPeak = p1.y - p0.y >= c.minDy && p1.y - p2.y >= c.minDy;   // y vokser (ned) så avtar (opp)
        const onTable = Math.abs(p1.y - c.surfaceY) <= c.band && p1.x >= c.leftX - c.band && p1.x <= c.rightX + c.band;
        if (isPeak && onTable && (!lastB || p1.t - lastB.t >= c.minGapMs)) {
          const b = { t: p1.t, x: p1.x, y: p1.y, side: p1.x < c.netX ? 'left' : 'right' };
          this.bounces.push(b);
          events.push({ type: 'bounce', bounce: b });
        }
      }
    }
    if (this.bounces.length) {
      const last = this.bounces[this.bounces.length - 1];
      if (t - this.lastSeen > c.lostMs || t - last.t > c.idleMs) {
        const winnerSide = last.side === 'left' ? 'right' : 'left';
        events.push({ type: 'point', winnerSide, lastBounceSide: last.side,
          bounceCount: this.bounces.length, bounces: this.bounces.slice(), track: this.track.slice() });
        this.reset();
      }
    } else if (this.track.length && t - this.lastSeen > c.lostMs) {
      this.reset();                                                         // ball sett, men aldri sprettet: ingen poeng
    }
    return events;
  }
}

/**
 * Finner ballen i en frame: piksler som både har ballfargen og beveger seg.
 * state.prev holder forrige frames gråtoner. Returnerer {x,y,n} eller null.
 */
function detectBall(data, w, h, state, ball, tol = 55, moveThr = 10) {
  const n = w * h, CELL = 10, cw = Math.ceil(w / CELL), ch = Math.ceil(h / CELL);
  const first = !state.prev;
  if (first) state.prev = new Uint8Array(n);
  const cnt = new Int32Array(cw * ch), sx = new Float64Array(cw * ch), sy = new Float64Array(cw * ch);
  const tol2 = tol * tol;
  for (let p = 0, i = 0; p < n; p++, i += 4) {
    const r = data[i], g = data[i + 1], b = data[i + 2];
    const gray = (r * 77 + g * 150 + b * 29) >> 8;
    const moved = !first && Math.abs(gray - state.prev[p]) >= moveThr;
    state.prev[p] = gray;
    if (!moved) continue;
    const dr = r - ball.r, dg = g - ball.g, db = b - ball.b;
    if (dr * dr + dg * dg + db * db > tol2) continue;
    const x = p % w, y = (p / w) | 0, k = ((y / CELL) | 0) * cw + ((x / CELL) | 0);
    cnt[k]++; sx[k] += x; sy[k] += y;
  }
  let best = -1, bestSum = 0;                                               // tetteste 3x3-område av celler
  for (let cy = 0; cy < ch; cy++) for (let cx = 0; cx < cw; cx++) {
    let s = 0;
    for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
      const yy = cy + dy, xx = cx + dx;
      if (yy >= 0 && yy < ch && xx >= 0 && xx < cw) s += cnt[yy * cw + xx];
    }
    if (s > bestSum) { bestSum = s; best = cy * cw + cx; }
  }
  if (best < 0 || bestSum < 2 || bestSum > 400) return null;                // for lite = støy, for stort = ikke en ball
  const bx = best % cw, by = (best / cw) | 0;
  let tx = 0, ty = 0, tn = 0;
  for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
    const yy = by + dy, xx = bx + dx;
    if (yy >= 0 && yy < ch && xx >= 0 && xx < cw) { const k = yy * cw + xx; tx += sx[k]; ty += sy[k]; tn += cnt[k]; }
  }
  return { x: tx / tn, y: ty / tn, n: tn };
}

/** Tegner ballbanen for et poeng (bevis). ctx er en 2D-kontekst på w×h. */
function drawEvidence(ctx, w, h, cal, ev, label) {
  ctx.fillStyle = '#111'; ctx.fillRect(0, 0, w, h);
  ctx.strokeStyle = '#888'; ctx.lineWidth = 2;
  ctx.beginPath(); ctx.moveTo(cal.leftX, cal.surfaceY); ctx.lineTo(cal.rightX, cal.surfaceY); ctx.stroke();
  const net = (cal.leftX + cal.rightX) / 2;
  ctx.beginPath(); ctx.moveTo(net, cal.surfaceY); ctx.lineTo(net, cal.surfaceY - (cal.rightX - cal.leftX) * 0.07); ctx.stroke();
  ctx.strokeStyle = '#0a84ff'; ctx.lineWidth = 1.5; ctx.beginPath();
  ev.track.forEach((p, i) => i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y)); ctx.stroke();
  ctx.font = '11px sans-serif'; ctx.textAlign = 'center';
  ev.bounces.forEach((b, i) => {
    ctx.fillStyle = b.side === 'left' ? '#ff9f0a' : '#30d158';
    ctx.beginPath(); ctx.arc(b.x, b.y, 4, 0, 7); ctx.fill();
    ctx.fillStyle = '#fff'; ctx.fillText(String(i + 1), b.x, b.y - 8);
  });
  ctx.textAlign = 'left'; ctx.fillStyle = '#fff'; ctx.font = '12px sans-serif'; ctx.fillText(label, 6, 14);
}

const api = { RallyEngine, detectBall, drawEvidence };
if (typeof module !== 'undefined') module.exports = api;
if (typeof document === 'undefined') return;

// ======================= UI =======================
const $ = s => document.querySelector(s);
const W = 320, KEY = 'ppk-cam';
const work = document.createElement('canvas');
const wctx = work.getContext('2d', { willReadFrequently: true });
let H = 180, stream = null, running = false, engine = null, tracker = { prev: null };
let cal = loadCal(), calStep = -1, lastPt = null, lastFrameT = 0, fps = 0, bounceFlash = [];
let pending = null, tick = null;
const log = [];

function loadCal() { try { return JSON.parse(localStorage.getItem(KEY)) || {}; } catch { return {}; } }
function saveCal() { try { localStorage.setItem(KEY, JSON.stringify(cal)); } catch {} }
const calReady = () => cal.leftX != null && cal.rightX != null && cal.ball;
const status = t => { $('#camStatus').textContent = t; };

function makeEngine() {
  engine = calReady() ? new RallyEngine({ leftX: cal.leftX, rightX: cal.rightX, surfaceY: cal.surfaceY }) : null;
}

async function start() {
  try {
    stream = await navigator.mediaDevices.getUserMedia({ video: { width: { ideal: 1280 }, height: { ideal: 720 }, frameRate: { ideal: 60 } }, audio: false });
  } catch (e) {
    status('Fikk ikke tilgang til kamera: ' + e.message + (location.protocol === 'http:' && !['localhost', '127.0.0.1'].includes(location.hostname)
      ? ' (Nettleseren krever localhost eller HTTPS for kamera – åpne siden på http://localhost:5026 på maskinen med kameraet.)' : ''));
    return;
  }
  const v = $('#camVideo'); v.srcObject = stream; await v.play();
  H = Math.round(W * v.videoHeight / v.videoWidth); work.width = W; work.height = H;
  const ov = $('#camOverlay'); ov.width = W; ov.height = H;
  const fr = stream.getVideoTracks()[0].getSettings().frameRate;
  $('#camFps').textContent = fr ? `Kamera: ${Math.round(fr)} bilder/sek` : '';
  running = true; tracker = { prev: null }; makeEngine();
  $('#camStart').style.display = 'none'; $('#camStop').style.display = '';
  status(calReady() ? 'Kamera på. Sporer ballen.' : 'Kamera på. Trykk «Kalibrer» og følg stegene.');
  loop();
}
function stop() {
  running = false; stream?.getTracks().forEach(t => t.stop()); stream = null;
  $('#camVideo').srcObject = null; $('#camStart').style.display = ''; $('#camStop').style.display = 'none';
  $('#camOverlay').getContext('2d').clearRect(0, 0, W, H); status('Kamera av.');
}

function loop() {
  if (!running) return;
  const v = $('#camVideo');
  const next = () => v.requestVideoFrameCallback ? v.requestVideoFrameCallback(frame) : requestAnimationFrame(frame);
  function frame() {
    if (!running) return;
    const now = performance.now();
    if (lastFrameT) fps = fps * 0.9 + (1000 / (now - lastFrameT)) * 0.1;
    lastFrameT = now;
    wctx.drawImage(v, 0, 0, W, H);
    if (calReady() && engine) {
      const img = wctx.getImageData(0, 0, W, H);
      const pt = detectBall(img.data, W, H, tracker, cal.ball);
      lastPt = pt;
      for (const ev of engine.push(now, pt)) {
        if (ev.type === 'bounce') bounceFlash.push({ ...ev.bounce, until: now + 600 });
        if (ev.type === 'point') onPoint(ev);
      }
    }
    draw(now);
    next();
  }
  next();
}

function draw(now) {
  const o = $('#camOverlay').getContext('2d'); o.clearRect(0, 0, W, H);
  if (cal.leftX != null) {
    o.strokeStyle = '#ffd60a'; o.lineWidth = 1.5; o.beginPath();
    o.moveTo(cal.leftX, cal.surfaceY ?? cal.leftY); o.lineTo(cal.rightX ?? cal.leftX, cal.surfaceY ?? cal.leftY); o.stroke();
    if (cal.rightX != null) { const n = (cal.leftX + cal.rightX) / 2; o.beginPath(); o.moveTo(n, cal.surfaceY - 30); o.lineTo(n, cal.surfaceY + 10); o.stroke(); }
  }
  if (engine) { o.strokeStyle = '#0a84ff'; o.beginPath(); const tr = engine.track.filter(p => now - p.t < 500); tr.forEach((p, i) => i ? o.lineTo(p.x, p.y) : o.moveTo(p.x, p.y)); o.stroke(); }
  if (lastPt) { o.strokeStyle = '#30d158'; o.lineWidth = 2; o.beginPath(); o.arc(lastPt.x, lastPt.y, 7, 0, 7); o.stroke(); }
  bounceFlash = bounceFlash.filter(b => b.until > now);
  bounceFlash.forEach(b => { o.fillStyle = 'rgba(255,159,10,.9)'; o.beginPath(); o.arc(b.x, b.y, 6, 0, 7); o.fill(); });
  if (calStep >= 0) { o.fillStyle = 'rgba(0,0,0,.55)'; o.fillRect(0, 0, W, 22); o.fillStyle = '#fff'; o.font = '12px sans-serif'; o.fillText(['Trykk på venstre bordkant', 'Trykk på høyre bordkant', 'Trykk på ballen (hold den i bildet)'][calStep], 8, 15); }
  $('#camFps').textContent = running ? `Analyse: ${Math.round(fps)} bilder/sek${fps && fps < 40 ? ' – for lavt, ballen kan bli borte mellom bildene' : ''}` : '';
}

// ---- Kalibrering: tre trykk ----
$('#camOverlay').addEventListener('click', e => {
  if (calStep < 0 || !running) return;
  const r = e.target.getBoundingClientRect(), x = (e.clientX - r.left) * W / r.width, y = (e.clientY - r.top) * H / r.height;
  if (calStep === 0) { cal.leftX = x; cal.leftY = y; }
  else if (calStep === 1) { cal.rightX = x; cal.surfaceY = (cal.leftY + y) / 2; }
  else {
    const d = wctx.getImageData(Math.max(0, x - 2) | 0, Math.max(0, y - 2) | 0, 5, 5).data; let R = 0, G = 0, B = 0;
    for (let i = 0; i < d.length; i += 4) { R += d[i]; G += d[i + 1]; B += d[i + 2]; }
    const n = d.length / 4; cal.ball = { r: R / n, g: G / n, b: B / n };
  }
  calStep++;
  if (calStep > 2) { calStep = -1; saveCal(); makeEngine(); tracker = { prev: null }; status('Kalibrert. Prøv å sprette ballen på bordet og se at den blir sporet (grønn ring).'); }
});
$('#camCal').onclick = () => { if (!running) return status('Start kameraet først.'); cal = { ...cal, ball: null }; calStep = 0; };
$('#camMode').onchange = () => { try { localStorage.setItem(KEY + '-mode', $('#camMode').value); } catch {} };
$('#camSwap').onchange = () => { try { localStorage.setItem(KEY + '-swap', $('#camSwap').checked ? '1' : ''); } catch {} };
try { $('#camMode').value = localStorage.getItem(KEY + '-mode') || 'auto'; $('#camSwap').checked = !!localStorage.getItem(KEY + '-swap'); } catch {}
$('#camStart').onclick = start; $('#camStop').onclick = stop;

// ---- Poeng: forslag med angrefrist ----
const keyFor = side => ($('#camSwap').checked ? (side === 'left' ? 'b' : 'a') : (side === 'left' ? 'a' : 'b'));
const SIDE = { left: 'venstre', right: 'høyre' };
const AUTO_MS = 4000;

function onPoint(ev) {
  if (pending) applyPending();                                              // forrige poeng bekreftes først
  const key = keyFor(ev.winnerSide);
  const names = window.ppk?.names();
  const who = names ? names[key] : (key === 'a' ? 'Spiller 1' : 'Spiller 2');
  const reason = `Ballen traff ${SIDE[ev.lastBounceSide]} side sist (${ev.bounceCount} sprett) og kom ikke tilbake.`;
  const c = document.createElement('canvas'); c.width = W; c.height = H;
  drawEvidence(c.getContext('2d'), W, H, { ...cal }, ev, `Poeng til ${who}`);
  const mode = $('#camMode').value;
  // Auto kun når logikken er trygg (minst to sprett); ellers må du bekrefte selv.
  const auto = mode === 'auto' && ev.bounceCount >= 2;
  pending = { key, who, reason, img: c.toDataURL('image/jpeg', 0.7), until: auto ? performance.now() + AUTO_MS : null };
  renderPending(); clearInterval(tick);
  tick = setInterval(() => {
    if (!pending) return clearInterval(tick);
    if (pending.until && performance.now() >= pending.until) applyPending(); else renderPending();
  }, 200);
}
function renderPending() {
  const html = !pending ? '' : `
    <div class="cam-prop">
      <img src="${pending.img}" alt="Ballbane">
      <div><b>${esc(pending.who)} scoret?</b><br><span>${esc(pending.reason)}</span>
        ${pending.until ? `<br><span>Registreres om ${Math.max(0, Math.ceil((pending.until - performance.now()) / 1000))} sek</span>` : '<br><span>Trykk for å bekrefte</span>'}
        <div class="cam-btns"><button data-cam="ok">${pending.until ? 'Registrer nå' : 'Bekreft'}</button><button data-cam="no">Nei, feil</button></div>
      </div>
    </div>`;
  $('#camProposal').innerHTML = html; $('#sbToast').innerHTML = html; $('#sbToast').style.display = pending ? 'block' : 'none';
}
async function applyPending() {
  const p = pending; pending = null; clearInterval(tick); renderPending();
  if (!p || !(await window.ppk?.ensure())) return;
  if (window.ppk.point(p.key)) addLog(p, true);
}
function addLog(p, counted) {
  log.unshift({ ...p, counted, at: new Date() }); if (log.length > 8) log.pop();
  $('#camLog').innerHTML = log.map(l => `<div class="cam-log"><img src="${l.img}" alt=""><div><b>${esc(l.who)}</b> fikk poeng · ${l.at.toLocaleTimeString('no')}<br><span>${esc(l.reason)}</span></div></div>`).join('');
}
document.addEventListener('click', e => {
  const a = e.target.dataset?.cam; if (!a || !pending) return;
  if (a === 'ok') applyPending(); else { addLog(pending, false); pending = null; clearInterval(tick); renderPending(); }
});
function esc(s) { return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
})();
