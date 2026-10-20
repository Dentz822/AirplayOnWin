namespace AirPlay.App.PcAudio;

/// <summary>The page Safari opens to listen to the PC (served by <see cref="PcAudioShare"/>).</summary>
internal static class ListenPage
{
    public const string Html = """
<!doctype html>
<html lang="zh-Hant">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<title>電腦的聲音</title>
<style>
  :root { color-scheme: dark; font-family: -apple-system, system-ui, sans-serif; }
  body { margin: 0; background: #111; color: #eee; padding: max(24px, env(safe-area-inset-top)) 20px 24px; }
  main { max-width: 420px; margin: 0 auto; text-align: center; }
  h1 { font-size: 22px; font-weight: 600; margin: 8px 0 4px; }
  #status { color: #aaa; min-height: 1.4em; margin: 6px 0 22px; }
  button { font: inherit; font-size: 20px; font-weight: 600; width: 100%; padding: 18px; border: 0;
           border-radius: 14px; background: #2c8cff; color: #fff; }
  button.stop { background: #444; }
  .meter { height: 8px; background: #2a2a2a; border-radius: 4px; overflow: hidden; margin: 26px 0 8px; }
  #level { height: 100%; width: 0; background: #34c759; transition: width 60ms linear; }
  #info { color: #777; font-size: 13px; min-height: 1.2em; }
  ul { text-align: left; color: #888; font-size: 13px; line-height: 1.6; padding-left: 20px; margin-top: 30px; }
</style>
</head>
<body>
<main>
  <h1>🔊 電腦的聲音</h1>
  <div id="status">按下按鈕開始收聽</div>
  <button id="go">▶ 開始收聽</button>
  <div class="meter"><div id="level"></div></div>
  <div id="info"></div>
  <ul>
    <li>iPhone 需要和電腦連同一個 Wi-Fi。</li>
    <li>螢幕鎖定或切換到其他 App 時，Safari 會停止播放；回到這頁會自動繼續。</li>
    <li>延遲約 0.1～0.2 秒。</li>
  </ul>
</main>
<script>
"use strict";
const TARGET = 0.12;   // seconds kept buffered
const MAX = 0.40;      // above this, skip ahead to stay live
const statusEl = document.getElementById("status");
const button = document.getElementById("go");
const levelEl = document.getElementById("level");
const infoEl = document.getElementById("info");

let ctx = null, node = null, ws = null, wanted = false, silentAudio = null;
let srcRate = 48000;
// Ring buffer (stereo, at the AudioContext's rate)
const SIZE = 48000 * 4;
const ringL = new Float32Array(SIZE), ringR = new Float32Array(SIZE);
let readPos = 0, writePos = 0;
let resamplePos = 0, lastL = 0, lastR = 0;   // linear resampler state
let underruns = 0, peak = 0;

const available = () => (writePos - readPos + SIZE) % SIZE;

function push(int16) {
  const frames = int16.length / 2;
  const step = srcRate / ctx.sampleRate;      // source samples per output sample
  let p = 0;
  for (let i = 0; i < frames; i++) {
    const l = int16[2 * i] / 32768, r = int16[2 * i + 1] / 32768;
    peak = Math.max(peak, Math.abs(l), Math.abs(r));
    if (step === 1) { write(l, r); continue; }
    // emit output samples that fall between the previous and this source sample
    while (resamplePos <= 1) {
      write(lastL + (l - lastL) * resamplePos, lastR + (r - lastR) * resamplePos);
      resamplePos += step;
    }
    resamplePos -= 1;
    lastL = l; lastR = r;
  }
  // stay live: if too much is buffered, drop down to the target
  const max = MAX * ctx.sampleRate, target = TARGET * ctx.sampleRate;
  if (available() > max) readPos = (writePos - Math.floor(target) + SIZE) % SIZE;
}

function write(l, r) {
  ringL[writePos] = l; ringR[writePos] = r;
  writePos = (writePos + 1) % SIZE;
  if (writePos === readPos) readPos = (readPos + 1) % SIZE; // overflow: drop oldest
}

function process(e) {
  const outL = e.outputBuffer.getChannelData(0), outR = e.outputBuffer.getChannelData(1);
  const n = outL.length;
  if (available() < n) { outL.fill(0); outR.fill(0); underruns++; return; }
  for (let i = 0; i < n; i++) {
    outL[i] = ringL[readPos]; outR[i] = ringR[readPos];
    readPos = (readPos + 1) % SIZE;
  }
}

function silentWavUrl() {
  // 0.5 s of silence: playing an <audio> element switches iOS to "playback", so the silent
  // switch doesn't mute Web Audio (older iOS without navigator.audioSession).
  const rate = 8000, samples = rate / 2, buf = new ArrayBuffer(44 + samples * 2), v = new DataView(buf);
  const str = (o, s) => [...s].forEach((c, i) => v.setUint8(o + i, c.charCodeAt(0)));
  str(0, "RIFF"); v.setUint32(4, 36 + samples * 2, true); str(8, "WAVEfmt ");
  v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
  v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
  str(36, "data"); v.setUint32(40, samples * 2, true);
  return URL.createObjectURL(new Blob([buf], { type: "audio/wav" }));
}

function connect() {
  if (!wanted) return;
  const url = (location.protocol === "https:" ? "wss://" : "ws://") + location.host + location.pathname + "/ws";
  ws = new WebSocket(url);
  ws.binaryType = "arraybuffer";
  statusEl.textContent = "連線中…";
  ws.onmessage = (m) => {
    if (typeof m.data === "string") {
      const hello = JSON.parse(m.data);
      srcRate = hello.rate;
      statusEl.textContent = "正在收聽電腦的聲音" + (hello.excludesSelf ? "" : "（含 iPhone 投影的聲音）");
      return;
    }
    push(new Int16Array(m.data));
  };
  ws.onclose = () => {
    if (!wanted) return;
    statusEl.textContent = "連線中斷，重新連線中…";
    setTimeout(connect, 2000);
  };
}

async function start() {
  wanted = true;
  button.textContent = "■ 停止";
  button.className = "stop";
  if (navigator.audioSession) navigator.audioSession.type = "playback";
  if (!silentAudio) { silentAudio = new Audio(silentWavUrl()); silentAudio.loop = true; }
  silentAudio.play().catch(() => {});
  if (!ctx) {
    ctx = new (window.AudioContext || window.webkitAudioContext)({ latencyHint: "interactive" });
    node = ctx.createScriptProcessor(1024, 0, 2);
    node.onaudioprocess = process;
    node.connect(ctx.destination);
  }
  await ctx.resume();
  readPos = writePos = 0; resamplePos = 0;
  connect();
}

function stop() {
  wanted = false;
  button.textContent = "▶ 開始收聽";
  button.className = "";
  statusEl.textContent = "已停止";
  if (ws) ws.close();
  if (silentAudio) silentAudio.pause();
  if (ctx) ctx.suspend();
}

button.onclick = () => (wanted ? stop() : start());
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible" && wanted && ctx) ctx.resume();
});
setInterval(() => {
  levelEl.style.width = Math.min(100, peak * 140) + "%";
  peak *= 0.6;
  if (ctx && wanted) infoEl.textContent = `緩衝 ${(available() / ctx.sampleRate * 1000).toFixed(0)} ms · 中斷 ${underruns} 次`;
}, 100);
</script>
</body>
</html>
""";
}
