// node tools/test-wheel.cjs — 用本机 Edge 的真实滚轮输入验证注入脚本，无第三方依赖。
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { setTimeout: delay } = require('node:timers/promises');

const source = fs.readFileSync(path.join(__dirname, '../Web/page.js'), 'utf8');
const tempRoot = path.join(os.tmpdir(), 'opencode');
fs.mkdirSync(tempRoot, { recursive:true });
const profile = fs.mkdtempSync(path.join(tempRoot, 'BiliViewer-wheel-'));
const edge = process.env.EDGE_PATH || path.join(process.env['ProgramFiles(x86)'],
  'Microsoft/Edge/Application/msedge.exe');
const browser = spawn(edge, ['--headless=new', '--no-first-run', '--no-default-browser-check',
  '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' });
let socket, sequence = 0;
const pending = new Map();
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const id = ++sequence;
  pending.set(id, { resolve, reject });
  socket.send(JSON.stringify({ id, method, params }));
});
const evaluate = async expression => {
  const response = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
  assert.equal(response.exceptionDetails, undefined, JSON.stringify(response.exceptionDetails));
  return response.result.value;
};
const move = (x, y) => send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y });
const wheel = (x, y, deltaY = 100, modifiers = 0, deltaX = 0) =>
  send('Input.dispatchMouseEvent', { type: 'mouseWheel', x, y, deltaX, deltaY, modifiers });

async function fixture(nested = false, scrolled = false) {
  await send('Page.navigate', { url: 'about:blank' });
  await delay(100);
  await send('Page.setDocumentContent', { frameId: (await send('Page.getFrameTree')).frameTree.frame.id, html: `
    <!doctype html><html style='scroll-behavior:smooth'><head><style>
    body { margin:0; height:3000px; }
    #scroller { ${nested ? 'height:400px;overflow:auto;' : ''} }
    .bpx-player-container { width:75%; height:300px; background:#222; }
    video { width:100%; height:100%; }
    .spacer { height:1000px; }
    .bili-video-card { position:fixed; right:10px; top:10px; width:80px; height:80px; }
    </style></head><body><div id=scroller style='${nested ? 'overflow:auto!important;scrollbar-gutter:stable both-edges;' : ''}'>
    <div class=bpx-player-container><video></video></div><div class=spacer></div></div>
    <div class=bili-video-card><video></video></div></body></html>` });
  await evaluate(source);
  if (scrolled) await evaluate(`window.scrollTo({ top:80, behavior:'instant' });
    document.querySelector('#scroller').scrollTop = ${nested ? 40 : 0};`);
  await evaluate(`document.querySelector('video').volume = 0.5;
    window.baseline = { y:scrollY, x:scrollX, nested:document.querySelector('#scroller').scrollTop,
      width:document.querySelector('.bpx-player-container').getBoundingClientRect().width };
    window.maxDrift = 0;
    (function watch() {
      maxDrift = Math.max(maxDrift, Math.abs(scrollY-baseline.y), Math.abs(scrollX-baseline.x),
        Math.abs(document.querySelector('#scroller').scrollTop-baseline.nested));
      window.wheelTestFrame = requestAnimationFrame(watch);
    })();`);
  await move(100, 120);
  await delay(50);
}

async function main() {
  browser.on('error', error => { throw error; });
  let port;
  for (let i = 0; i < 100; i++) {
    try { port = fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]; break; }
    catch { await delay(100); }
  }
  assert.ok(port, 'Edge did not start its debugging endpoint');
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  socket = new WebSocket(pages.find(page => page.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject; });
  socket.onmessage = event => {
    const message = JSON.parse(event.data);
    const call = pending.get(message.id);
    if (!call) return;
    pending.delete(message.id);
    if (message.error) call.reject(new Error(JSON.stringify(message.error)));
    else call.resolve(message.result);
  };
  await send('Page.enable');
  await send('Emulation.setDeviceMetricsOverride', { width:1000, height:700, deviceScaleFactor:1, mobile:false });

  for (const [nested, scrolled] of [[false, false], [true, false], [false, true], [true, true]]) {
    await fixture(nested, scrolled);
    assert.equal(await evaluate(`getComputedStyle(document.documentElement).overflowY`), 'hidden', 'lock before first wheel');
    assert.equal(await evaluate(`document.querySelector('.bpx-player-container').getBoundingClientRect().width === baseline.width`), true, 'no layout shift on hover');
    if (nested) assert.equal(await evaluate(`getComputedStyle(document.querySelector('#scroller')).overflowY`), 'hidden');
    for (const delta of [100, 100, -100, 3, -3, 500, -500]) await wheel(100, 120, delta);
    await wheel(100, 120, 0, 0, 100);
    await delay(600);
    assert.equal(await evaluate('maxDrift'), 0, 'no page or nested scroll, including intermediate frames');
    assert.ok(await evaluate('__bvStats.wheel >= 8'));
    assert.ok(await evaluate(`document.querySelector('video').volume !== 0.5`), 'volume changes');
    await wheel(100, 120, 100, 1);
    assert.equal(await evaluate('__bvStats.sysvol'), 1, 'Alt wheel remains handled');
    assert.equal(await evaluate('maxDrift'), 0);

    // 守卫尚未到期时移出，外部第一格滚轮必须立即恢复页面滚动。
    await move(900, 450);
    await wheel(900, 450, 100);
    await delay(500);
    assert.ok(await evaluate('scrollY > baseline.y'), 'outside wheel scrolls immediately');
    assert.equal(await evaluate('document.documentElement.style.overflow'), '');
    assert.equal(await evaluate('document.documentElement.style.scrollBehavior'), 'smooth');
    if (nested) {
      assert.equal(await evaluate(`document.querySelector('#scroller').style.getPropertyValue('overflow')`), 'auto');
      assert.equal(await evaluate(`document.querySelector('#scroller').style.getPropertyPriority('overflow')`), 'important');
      assert.equal(await evaluate(`document.querySelector('#scroller').style.scrollbarGutter`), 'stable both-edges');
    }
    console.log(`PASS: ${nested ? 'nested scroller' : 'document'}${scrolled ? ' already scrolled' : ''}: zero frame drift, volume, Alt, layout, outside scroll and style restoration`);
  }

  await fixture();
  await evaluate(`document.elementFromPoint(100,120).dispatchEvent(new WheelEvent('wheel', {
    clientX:100, clientY:120, deltaY:100, bubbles:true, cancelable:false }));`);
  await delay(500);
  assert.equal(await evaluate(`document.querySelector('video').volume`), 0.48);
  assert.equal(await evaluate(`getComputedStyle(document.documentElement).overflowY`), 'hidden');
  assert.equal(await evaluate('maxDrift'), 0);
  await evaluate(`window.dispatchEvent(new Event('blur'));`);
  assert.equal(await evaluate('document.documentElement.style.overflow'), '', 'window blur releases the lock');
  console.log('PASS: non-cancelable wheel and unlock on window blur');

  await fixture();
  await wheel(100, 120);
  const handled = await evaluate('__bvStats.wheel');
  await wheel(100, 120, 100, 2);
  assert.equal(await evaluate('__bvStats.wheel'), handled, 'Ctrl wheel is left to the browser');
  assert.equal(await evaluate('document.documentElement.style.overflow'), '', 'Ctrl wheel releases the lock');
  console.log('PASS: Ctrl wheel releases the lock without changing volume');

  await fixture();
  await move(950, 40);
  assert.equal(await evaluate('document.documentElement.style.overflow'), '', 'card preview does not lock the document');
  await wheel(950, 40);
  await delay(500);
  assert.equal(await evaluate('__bvStats.wheel'), 0, 'card preview does not change volume');
  assert.ok(await evaluate('scrollY > 0'), 'card preview scrolls normally');
  console.log('PASS: preview cards preserve normal scrolling');
}

main().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (socket && socket.readyState === WebSocket.OPEN) {
    await send('Browser.close').catch(() => {});
    socket.close();
  }
  if (browser.exitCode === null) await Promise.race([new Promise(resolve => browser.once('exit', resolve)), delay(3000)]);
  if (browser.exitCode === null) browser.kill();
  try { fs.rmSync(profile, { recursive:true, force:true, maxRetries:10, retryDelay:100 }); }
  catch { console.warn(`Temporary browser profile could not be removed: ${profile}`); }
});
