/* BiliViewer for Windows —— 页面内交互脚本
 *
 * 全部由 C# 通过 CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync 注入，顶层页面和所有 iframe 都会执行。
 * 负责：
 *   1. 鼠标停在播放器上滚轮 = 调视频音量（不滚动页面）
 *   2. Alt+滚轮 = 调系统音量（发消息给原生，原生调 CoreAudio 后回推 HUD 数值）
 *   3. 播放器上单击 = 暂停/播放（260ms 延迟，给双击留窗口），双击 = 网页全屏
 *   4. 网页全屏状态下的 ESC 处理 + 通知原生退出窗口全屏
 *   5. 鼠标侧键 3/4 = 后退/前进
 *   6. 音量 HUD（页面内 DOM，1s 后淡出；HTML5 全屏时重新挂到 fullscreen 元素上）
 *   7. 供原生侧调用的跨 frame 聚合入口：__bvPaused / __bvSetPaused / __bvScreenState / __bvClickWebFS
 *
 * 手感参数沿用 macOS 版（Sources/BrowserWindow.swift 里的 VolumeWebView）：
 *   GAIN_PRECISE = 0.0012（精确设备按像素）、GAIN_WHEEL_LINE = 0.006（按行，单次最多 3 行）、MAX_STEP = 0.01
 */
(function () {
  'use strict';

  if (window.__bvInstalled) return;
  window.__bvInstalled = true;

  // 卡片上的「悬停预览」video 不算播放器
  var PREVIEW_SELECTOR = '[class*="inline-player"], .bili-video-card, .bili-video-card__wrap, ' +
    '.bili-feed-card, .feed-card, .video-page-card-small, .bili-dyn-card, .bili-dyn-card-video, ' +
    '.bili-live-card, .bili-cover-card, .recommend-list-v1, .rec-list';

  var PLAYER_SELECTOR = '.bpx-player-container, .bilibili-player, #live-player, #player-ctnr, ' +
    '.player-section, .live-player, .bpx-player-video-area';

  // 控制条 / 按钮区域：不当作「点视频」处理
  var CONTROL_SELECTOR = '.bpx-player-control-bottom, .bpx-player-control-top, .bpx-player-ctrl, ' +
    '.squirtle-controller, .bilibili-player-video-control, .bpx-player-sending-area, .bpx-player-toast, ' +
    '.bpx-player-dm-setting, .bpx-player-dm-input, .bpx-player-dm-btn, .bpx-player-ending-panel, ' +
    'button, a, input, textarea, select, [role="button"], [contenteditable="true"]';

  var GAIN_PRECISE = 0.0012;   // 精确指针（触摸板）：每像素
  var GAIN_WHEEL_LINE = 0.006; // 滚轮：每行
  var MAX_STEP = 0.01;         // 单次事件最多 1%（mac 版手感）
  var NOTCH_STEP = 0.02;       // Windows 鼠标一格滚轮（Chromium 固定 100px）算两倍，否则太慢
  var SYS_GAIN_PRECISE = 0.0008;
  var SYS_GAIN_NOTCH = 0.02;
  var SYS_MAX_STEP = 0.06;

  var CLICK_DELAY = 260;       // 单击延迟，给双击留窗口
  var HUD_LINGER = 1000;       // HUD 停留时间

  var sensitivity = (typeof window.__bvSensitivity === 'number' && window.__bvSensitivity > 0)
    ? window.__bvSensitivity : 1.0;

  // 统计量：方便原生侧/调试用 ExecuteScript 读出来确认事件确实被处理
  var stats = {
    wheel: 0, click: 0, dblclick: 0, nav: 0, sysvol: 0, scrollFix: 0,
    lastVolume: -1, lastScreen: 'normal'
  };
  window.__bvStats = stats;

  function clamp(v, lo, hi) { return v < lo ? lo : (v > hi ? hi : v); }

  function post(name, body) {
    try {
      if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
        window.chrome.webview.postMessage({ name: name, body: body == null ? '' : String(body) });
      }
    } catch (e) { /* ignore */ }
  }

  // ───────────────────────── 播放器识别 ─────────────────────────

  function isPreview(el) {
    if (!el) return false;
    try {
      if (el.closest && el.closest(PREVIEW_SELECTOR)) return true;
    } catch (e) { /* ignore */ }
    return false;
  }

  function isControl(el) {
    if (!el) return false;
    try {
      if (el.closest && el.closest(CONTROL_SELECTOR)) return true;
    } catch (e) { /* ignore */ }
    return false;
  }

  /** 当前文档里最像「主播放器」的 video：跳过卡片预览，取分辨率面积最大的 */
  function findMainVideo() {
    var best = null;
    var bestArea = -1;
    var list;
    try { list = document.querySelectorAll('video'); } catch (e) { return null; }

    for (var i = 0; i < list.length; i++) {
      var v = list[i];
      if (!v || isPreview(v)) continue;
      var area = (v.videoWidth || 0) * (v.videoHeight || 0);
      if (area > bestArea) { best = v; bestArea = area; }
    }
    return best;
  }

  /** 记录最近一次被指针指到的主视频，供全屏切换、聚合查询使用 */
  function rememberMain(v) {
    if (v) window.__biliViewerMainVideo = v;
  }

  /**
   * 返回坐标下的播放器元素（video 或容器）。
   * 顺序：命中点所在元素 → 是预览卡片则忽略 → 找最近播放器容器 → 兜底用主视频矩形。
   */
  function findPlayer(x, y) {
    var el = null;
    try { el = document.elementFromPoint(x, y); } catch (e) { el = null; }
    if (!el) return null;
    if (isPreview(el)) return null;

    var container = null;
    try { container = el.closest ? el.closest(PLAYER_SELECTOR) : null; } catch (e) { container = null; }
    if (container) return container;

    var main = findMainVideo();
    if (main) {
      var r = main.getBoundingClientRect();
      if (r.width > 200 && r.height > 120 &&
          x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) {
        rememberMain(main);
        return main;
      }
    }
    return null;
  }

  /** 在给定播放器对象里找要调音量的 video */
  function videoFor(target) {
    if (!target) return null;
    if (target.tagName === 'VIDEO') return target;
    var v = target.querySelector ? target.querySelector('video') : null;
    if (v && !isPreview(v)) return v;
    return null;
  }

  // ───────────────────────── 音量 HUD ─────────────────────────

  var hudEl = null;
  var hudTimer = null;
  var hudFadeTimer = null;

  var SPEAKER_SVG =
    '<svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2" ' +
    'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
    '<path d="M11 5 6 9H3v6h3l5 4V5z" fill="#fff" stroke="none"></path>' +
    '<path d="M15.5 8.5a5 5 0 0 1 0 7"></path>' +
    '<path d="M18.5 5.5a9 9 0 0 1 0 13"></path></svg>';

  function hudParent() {
    return document.fullscreenElement || document.documentElement || document.body;
  }

  function createHud() {
    var el = document.createElement('div');
    el.id = '__bv_hud';
    el.style.cssText = [
      'position:fixed', 'z-index:2147483000', 'pointer-events:none', 'display:none',
      'align-items:center', 'gap:10px', 'padding:12px 20px 12px 16px',
      'background:rgba(12,12,14,.68)', 'border-radius:999px', 'color:#fff',
      'font:600 30px/1 -apple-system,"Segoe UI",system-ui,sans-serif',
      'letter-spacing:.5px', 'box-shadow:0 8px 28px rgba(0,0,0,.35)',
      'backdrop-filter:blur(8px)', '-webkit-backdrop-filter:blur(8px)',
      'opacity:0', 'transition:opacity .16s ease', 'transform:translate(-50%,-50%)'
    ].join(';');
    el.innerHTML = '<span style="display:flex;width:22px;height:22px">' + SPEAKER_SVG + '</span>' +
      '<span class="__bv_hud_num">100%</span>';
    return el;
  }

  function showHud(volume, target) {
    var parent = hudParent();
    if (!parent) return;

    if (!hudEl || !hudEl.parentNode) hudEl = createHud();
    if (hudEl.parentNode !== parent) parent.appendChild(hudEl);

    var num = hudEl.querySelector('.__bv_hud_num');
    if (num) num.textContent = Math.round(clamp(volume, 0, 1) * 100) + '%';

    // 位置：优先播放器中心，拿不到就视口中心
    var left = window.innerWidth / 2;
    var top = window.innerHeight / 2;
    if (target && target.getBoundingClientRect) {
      var r = target.getBoundingClientRect();
      if (r.width > 40 && r.height > 40) {
        left = r.left + r.width / 2;
        top = r.top + r.height / 2;
      }
    }
    hudEl.style.left = left + 'px';
    hudEl.style.top = top + 'px';
    hudEl.style.display = 'flex';
    hudEl.style.opacity = '1';

    if (hudTimer) clearTimeout(hudTimer);
    if (hudFadeTimer) clearTimeout(hudFadeTimer);
    hudTimer = setTimeout(function () {
      if (!hudEl) return;
      hudEl.style.opacity = '0';
      hudFadeTimer = setTimeout(function () {
        if (hudEl) hudEl.style.display = 'none';
      }, 200);
    }, HUD_LINGER);
  }

  // 原生侧调完系统音量后回推 {"hud": 0.42}
  try {
    if (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener) {
      window.chrome.webview.addEventListener('message', function (ev) {
        var d = ev && ev.data;
        if (d && typeof d === 'object' && typeof d.hud === 'number') {
          showHud(d.hud, hudTarget());
        }
      });
    }
  } catch (e) { /* ignore */ }

  var lastPlayedTarget = null;
  function hudTarget() { return lastPlayedTarget; }

  // ───────────────────────── 滚轮：调音量 ─────────────────────────

  // Chromium（WebView2）派发真实滚轮事件时 cancelable=false（实测，无论监听挂在 document
  // 还是 window、是否 passive:false），preventDefault 一律无效，页面照样被默认滚动。
  // scroll 回调发生时合成器可能已经画出一帧，事后拨回仍会轻微抖动。
  // 指针进入播放器时就锁住滚动祖先，提前阻止默认滚动；原有位置守卫只作兜底。
  var GUARD_SETTLE = 320;
  var scrollGuardBusy = false, scrollGuardX = 0, scrollGuardY = 0;
  var scrollGuardTimer = 0;
  var scrollBehaviorSaved = null; // { root: [value, priority], body: [value, priority] }
  var scrollHoverTarget = null, scrollLockTarget = null;
  var scrollLocks = [];

  function unlockScroll() {
    for (var i = scrollLocks.length - 1; i >= 0; i--) {
      var saved = scrollLocks[i];
      for (var j = 0; j < saved.props.length; j++) {
        var prop = saved.props[j];
        if (prop.value) saved.el.style.setProperty(prop.name, prop.value, prop.priority);
        else saved.el.style.removeProperty(prop.name);
      }
    }
    scrollLocks = [];
    scrollLockTarget = null;
  }

  function lockScroll(target) {
    if (scrollLockTarget === target) return;
    unlockScroll();
    scrollLockTarget = target;
    for (var el = target; el; el = el.parentElement) {
      var css = window.getComputedStyle(el);
      if (el !== document.scrollingElement &&
          !((el.scrollHeight > el.clientHeight && /auto|scroll/.test(css.overflowY)) ||
            (el.scrollWidth > el.clientWidth && /auto|scroll/.test(css.overflowX)))) continue;
      var props = ['overflow-x', 'overflow-y', 'scrollbar-gutter', 'overscroll-behavior'];
      var saved = { el: el, props: [] };
      for (var i = 0; i < props.length; i++) {
        saved.props.push({ name: props[i], value: el.style.getPropertyValue(props[i]),
                           priority: el.style.getPropertyPriority(props[i]) });
      }
      scrollLocks.push(saved);
      // 保留原有滚动条占位，避免锁定本身使播放器横向挪动。
      var hasScrollbar = el === document.scrollingElement
        ? window.innerWidth > el.clientWidth
        : el.offsetWidth > el.clientWidth + parseFloat(css.borderLeftWidth) + parseFloat(css.borderRightWidth);
      if (css.scrollbarGutter.indexOf('stable') === -1 && hasScrollbar) {
        el.style.setProperty('scrollbar-gutter', 'stable', 'important');
      }
      el.style.setProperty('overscroll-behavior', 'none', 'important');
      el.style.setProperty('overflow-x', 'hidden', 'important');
      el.style.setProperty('overflow-y', 'hidden', 'important');
    }
  }

  function onScrollPointer(e) {
    scrollHoverTarget = e.ctrlKey ? null : findPlayer(e.clientX, e.clientY);
    if (scrollHoverTarget) lockScroll(scrollHoverTarget);
    else leaveScrollPlayer();
  }

  function leaveScrollPlayer() {
    scrollHoverTarget = null;
    endScrollGuard();
    unlockScroll();
  }

  document.addEventListener('pointerover', onScrollPointer, { capture: true, passive: true });
  document.addEventListener('pointermove', onScrollPointer, { capture: true, passive: true });
  document.addEventListener('pointerleave', leaveScrollPlayer, { passive: true });
  window.addEventListener('blur', leaveScrollPlayer);

  function setScrollBehaviorInstant(on) {
    var root = document.documentElement, body = document.body;
    if (on) {
      if (scrollBehaviorSaved) return;
      scrollBehaviorSaved = { root: null, body: null };
      scrollBehaviorSaved.root = [root.style.getPropertyValue('scroll-behavior'),
                                  root.style.getPropertyPriority('scroll-behavior')];
      if (body) scrollBehaviorSaved.body = [body.style.getPropertyValue('scroll-behavior'),
                                            body.style.getPropertyPriority('scroll-behavior')];
      root.style.setProperty('scroll-behavior', 'auto', 'important');
      if (body) body.style.setProperty('scroll-behavior', 'auto', 'important');
      return;
    }
    if (!scrollBehaviorSaved) return;
    var saved = scrollBehaviorSaved;
    scrollBehaviorSaved = null;
    put(root, saved.root);
    if (body) put(body, saved.body);

    function put(el, pair) {
      if (!el || !pair) return;
      if (pair[0]) el.style.setProperty('scroll-behavior', pair[0], pair[1]);
      else el.style.removeProperty('scroll-behavior');
    }
  }

  function restoreScroll() {
    if (!scrollGuardBusy) return;
    if (window.scrollX === scrollGuardX && window.scrollY === scrollGuardY) return;
    stats.scrollFix++;
    setScrollBehaviorInstant(true);
    window.scrollTo(scrollGuardX, scrollGuardY);
  }

  function endScrollGuard() {
    if (scrollGuardTimer) clearTimeout(scrollGuardTimer);
    scrollGuardTimer = 0;
    if (!scrollGuardBusy) return;
    restoreScroll();
    scrollGuardBusy = false;
    // 还原页面原本的 scroll-behavior（没有内联值时直接去掉，样式表里的 smooth 自然回来）
    setScrollBehaviorInstant(false);
    if (!scrollHoverTarget || !scrollHoverTarget.isConnected) unlockScroll();
  }

  function guardScroll(target) {
    if (!scrollGuardBusy) {
      scrollGuardBusy = true;
      // 锚点只在守卫开始时取一次：连续滚轮中途页面若已被滚走，不能把新位置当成目标
      scrollGuardX = window.scrollX;
      scrollGuardY = window.scrollY;
      setScrollBehaviorInstant(true);
    } else {
      restoreScroll();
    }
    lockScroll(target);
    // 每一格滚轮都续期：最后一格落地的平滑动画也要被守到
    if (scrollGuardTimer) clearTimeout(scrollGuardTimer);
    scrollGuardTimer = setTimeout(endScrollGuard, GUARD_SETTLE);
  }

  // scroll 不冒泡，但捕获阶段能收到任意元素的滚动；有滚动就说明守卫漏了一帧，立刻拨回。
  document.addEventListener('scroll', restoreScroll, { capture: true, passive: true });

  function onWheel(e) {
    if (e.ctrlKey) { // Ctrl+滚轮留给浏览器缩放
      leaveScrollPlayer();
      return;
    }
    if (e.defaultPrevented && e.__bvHandled) return;

    var target = findPlayer(e.clientX, e.clientY);
    if (!target) {
      leaveScrollPlayer();
      return;
    }
    lastPlayedTarget = target;
    stats.wheel++;
    scrollHoverTarget = target;
    guardScroll(target);

    var negative = e.deltaY < 0;

    // Alt+滚轮 → 系统音量
    if (e.altKey) {
      e.preventDefault();
      e.stopPropagation();
      var sysStep;
      if (e.deltaMode === 0 && Math.abs(e.deltaY) >= 50) {
        sysStep = (negative ? 1 : -1) * SYS_GAIN_NOTCH;
      } else if (e.deltaMode === 0) {
        sysStep = (-e.deltaY) * SYS_GAIN_PRECISE * sensitivity;
      } else {
        var sysLines = Math.min(Math.abs(e.deltaY) || 1, 3);
        sysStep = (negative ? 1 : -1) * sysLines * SYS_GAIN_NOTCH * sensitivity;
      }
      sysStep = clamp(sysStep, -SYS_MAX_STEP, SYS_MAX_STEP);
      stats.sysvol++;
      post('sysvol', sysStep.toFixed(4));
      return;
    }

    var video = videoFor(target);
    if (!video) return;

    var step;
    if (e.deltaMode === 0 && Math.abs(e.deltaY) >= 50) {
      // Windows 鼠标一格固定 100px；按「格」给 2%，否则一格只有 1% 太慢
      step = (negative ? 1 : -1) * NOTCH_STEP * sensitivity;
    } else if (e.deltaMode === 0) {
      step = clamp((-e.deltaY) * GAIN_PRECISE * sensitivity, -MAX_STEP, MAX_STEP);
    } else {
      var lines = Math.min(Math.abs(e.deltaY) || 1, 3);
      step = clamp((negative ? 1 : -1) * lines * GAIN_WHEEL_LINE * sensitivity, -MAX_STEP, MAX_STEP);
    }

    var before = video.volume;
    var next = clamp(before + step, 0, 1);
    video.volume = next;
    if (video.muted && next > 0) video.muted = false;
    rememberMain(video);

    stats.lastVolume = video.volume;
    showHud(video.volume, target);

    e.preventDefault();
    e.stopPropagation();
  }

  // 挂在 document 上（capture + 非被动）：window 上的 wheel 监听实测拿到的
  // cancelable 更早被判定为非阻塞，挂在 document 上至少能让 preventDefault 在合成事件里生效。
  // 真实滚轮两条都拦不住，靠 guardScroll() 兜底。
  document.addEventListener('wheel', onWheel, { capture: true, passive: false });

  // ───────────────────────── 单击 / 双击 ─────────────────────────

  var clickTimer = null;
  var pendingClick = null;
  var clickWatch = null;

  function cancelClickWatch() {
    if (clickWatch) { clearInterval(clickWatch); clickWatch = null; }
  }

  function onMouseDown(e) {
    // 鼠标侧键（Chromium: 3=后退 4=前进）由 Chromium/WebView2 自己处理前进后退，
    // 这里绝不能再 post 给原生——否则一次按键会后退两格（实测）。
    if (e.button !== 0) return;
    cancelClickWatch();
    var target = findPlayer(e.clientX, e.clientY);
    if (!target) { pendingClick = null; return; }
    if (isControl(e.target)) { pendingClick = null; return; }

    lastPlayedTarget = target;
    pendingClick = {
      x: e.clientX, y: e.clientY,
      target: target,
      pausedBefore: pausedLocal(),
      screenBefore: screenStateLocal()
    };
  }

  /**
   * 单击的落点判定。
   * B 站播放器自己对「点击视频」也会切暂停（core.js 的 onPerchClick），而且偶尔会
   * 自己翻两次（先 pause 再 play，实测约 12% 的概率），所以不能只判一次：先决定
   * 「这一下要让状态翻一次」，然后在接下来 1.2s 里守住这个目标状态，谁再翻就纠回来。
   */
  function finishClick(pending) {
    var before = pending.pausedBefore;
    if (before === -1) return;
    var want = before === 1 ? 0 : 1;        // 期望的最终暂停状态
    if (pausedLocal() === before) setPausedLocal(want === 1);  // 页面没响应 → 我们自己来

    var deadline = Date.now() + 1200;
    var fixes = 0;
    cancelClickWatch();
    clickWatch = setInterval(function () {
      var now = pausedLocal();
      if (now === -1 || Date.now() > deadline || fixes > 3) { cancelClickWatch(); return; }
      if (now !== want) { setPausedLocal(want === 1); fixes++; }
    }, 100);
  }

  function onClickCapture(e) {
    // 只标记，不阻止默认行为；真正动作在定时器里做
    if (e.button !== 0) return;
    if (!pendingClick) return;
    if (isControl(e.target)) { pendingClick = null; return; }
    stats.click++;

    var pending = pendingClick;
    pendingClick = null;
    if (clickTimer) clearTimeout(clickTimer);

    clickTimer = setTimeout(function () {
      clickTimer = null;
      finishClick(pending);
    }, CLICK_DELAY);
  }

  function onDblClickCapture(e) {
    if (e.button !== 0) return;
    var target = findPlayer(e.clientX, e.clientY);
    if (!target || isControl(e.target)) return;

    stats.dblclick++;
    if (clickTimer) { clearTimeout(clickTimer); clickTimer = null; }
    cancelClickWatch();
    pendingClick = null;

    var screenBefore = screenStateLocal();
    var pausedBefore = pausedLocal();

    // 页面自己可能已经处理了双击全屏；稍等一下看状态是否变化，没变就由我们来切
    setTimeout(function () {
      if (screenStateLocal() === screenBefore) {
        clickWebFSLocal();
      }
      // B 站全屏会重建播放器，恢复原本的暂停状态
      keepPausedState(pausedBefore);
    }, 90);

    // 告诉原生「双击后网页是否处于全屏」，让窗口全屏跟着一起进/出
    post('dblclick', screenBefore === 'normal' ? 'full' : 'normal');
  }

  function onKeyDown(e) {
    if (e.key !== 'Escape' && e.keyCode !== 27) return;
    // 网页全屏（B 站自己的「网页全屏」，不是 HTML5 全屏）由我们退出
    if (screenStateLocal() !== 'normal') {
      clickWebFSLocal();
    }
    // 窗口全屏由原生退出
    post('escape', '');
  }

  window.addEventListener('mousedown', onMouseDown, { capture: true });
  window.addEventListener('click', onClickCapture, { capture: true });
  window.addEventListener('dblclick', onDblClickCapture, { capture: true });
  window.addEventListener('keydown', onKeyDown, { capture: true });

  // ───────────────────────── 暂停状态（本 frame） ─────────────────────────

  function pausedLocal() {
    var v = window.__biliViewerMainVideo || findMainVideo();
    if (!v) return -1;
    return v.paused ? 1 : 0;
  }

  function setPausedLocal(paused) {
    var v = window.__biliViewerMainVideo || findMainVideo();
    if (!v) return false;
    try {
      if (paused) v.pause();
      else v.play();
      return true;
    } catch (e) { return false; }
  }

  /** 全屏切换后 B 站会重建播放器并自动播放/暂停，这里把状态纠正回切换前 */
  function keepPausedState(pausedBefore) {
    if (pausedBefore === -1) return;
    var tries = 0;
    var timer = setInterval(function () {
      tries++;
      var now = pausedLocal();
      if (now !== -1 && now !== pausedBefore) setPausedLocal(pausedBefore === 1);
      if (tries >= 8) clearInterval(timer);
    }, 250);
  }

  // ───────────────────────── 网页全屏状态（本 frame） ─────────────────────────

  function screenStateLocal() {
    try {
      var el = document.querySelector('.bpx-player-container');
      if (!el) return 'normal';
      var s = el.getAttribute('data-screen') || 'normal';
      if (s === 'web') return 'web';
      if (s === 'full') return 'full';
      if (s === 'normal' || s === 'mini') return 'normal';
      return 'normal';
    } catch (e) {
      return 'normal';
    }
  }

  function clickWebFSLocal() {
    try {
      var btn = document.querySelector('.bpx-player-ctrl-web, .squirtle-video-pagefullscreen');
      if (btn) { btn.click(); return true; }
    } catch (e) { /* ignore */ }
    return false;
  }

  // 每个 frame 都暴露本地的版本，供顶层聚合
  window.__bvFindMain = findMainVideo;
  window.__bvPausedLocal = pausedLocal;
  window.__bvSetPausedLocal = setPausedLocal;
  window.__bvScreenStateLocal = screenStateLocal;
  window.__bvClickWebFSLocal = clickWebFSLocal;
  window.__bvSetSensitivity = function (v) {
    if (typeof v === 'number' && v > 0) sensitivity = v;
  };

  // ───────────────────────── 跨 frame 聚合（顶层调用） ─────────────────────────

  /** 深度优先遍历同源 frame 树；跨域 frame 会抛异常，直接跳过 */
  function eachFrame(fn) {
    (function walk(w) {
      try { fn(w); } catch (e) { /* ignore */ }
      var frames;
      try { frames = w.frames; } catch (e) { return; }
      if (!frames) return;
      for (var i = 0; i < frames.length; i++) {
        var child = null;
        try { child = frames[i]; } catch (e) { continue; }
        if (child) walk(child);
      }
    })(window);
  }

  window.__bvScreenState = function () {
    var best = 'normal';
    eachFrame(function (w) {
      if (typeof w.__bvScreenStateLocal !== 'function') return;
      var s = w.__bvScreenStateLocal();
      if (s === 'full') best = 'full';
      else if (s === 'web' && best === 'normal') best = 'web';
    });
    stats.lastScreen = best;
    return best;
  };

  window.__bvPaused = function () {
    var result = -1;
    eachFrame(function (w) {
      if (typeof w.__bvPausedLocal !== 'function') return;
      var p = w.__bvPausedLocal();
      if (p !== -1) result = p;
    });
    return result;
  };

  window.__bvSetPaused = function (paused) {
    var done = false;
    eachFrame(function (w) {
      if (typeof w.__bvSetPausedLocal !== 'function') return;
      if (w.__bvSetPausedLocal(!!paused)) done = true;
    });
    return done;
  };

  window.__bvClickWebFS = function () {
    var done = false;
    eachFrame(function (w) {
      if (typeof w.__bvScreenStateLocal !== 'function') return;
      if (w.__bvScreenStateLocal() === 'normal') return;
      if (typeof w.__bvClickWebFSLocal === 'function' && w.__bvClickWebFSLocal()) done = true;
    });
    return done;
  };

  // 进入 HTML5 全屏时把 HUD 挪进 fullscreen 元素，否则会被裁掉
  document.addEventListener('fullscreenchange', function () {
    if (!hudEl) return;
    var parent = hudParent();
    if (parent && hudEl.parentNode !== parent) parent.appendChild(hudEl);
  }, true);
})();
