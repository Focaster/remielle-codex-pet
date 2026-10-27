/**
 * 小蕾米桌宠 — DSH 客户端半区
 *
 * 由宿主注入 <script defer src="/dsh-remielle/client.js"> 加载。
 * 图集：1536×2288，8 列 × 11 行，每格 192×208（Codex v2 精灵图格式）。
 *   行 0 idle(7)   1 向右跑(8)   2 向左跑(8)   3 招手(4)   4 得意(5)
 *   行 5 失败(8)   6 等待(6)     7 工作中(6)   8 检查中(6)  9/10 16 向视线
 */
(function () {
  'use strict'
  if (window.__DSH_REMIELLE_PET__) return
  window.__DSH_REMIELLE_PET__ = true

  var SCALE = 0.5
  var CW = 192
  var CH = 208
  var DW = CW * SCALE
  var DH = CH * SCALE
  var COLS = 8
  var ROWS = 11

  var API = {
    state: '/dsh-remielle/state.json',
    sheet: '/dsh-remielle/spritesheet.webp',
  }

  var ANIMS = {
    idle: { row: 0, frames: 7, fps: 8, loop: true },
    runR: { row: 1, frames: 8, fps: 12, loop: true },
    runL: { row: 2, frames: 8, fps: 12, loop: true },
    wave: { row: 3, frames: 4, fps: 10, loop: false },
    smug: { row: 4, frames: 5, fps: 9, loop: false },
    failed: { row: 5, frames: 8, fps: 10, loop: false },
    waiting: { row: 6, frames: 6, fps: 5, loop: true },
    working: { row: 7, frames: 6, fps: 11, loop: true },
    review: { row: 8, frames: 6, fps: 8, loop: true },
  }

  var LINES = [
    '小蕾米来啦～',
    '诶嘿～',
    '今天也要加油哦',
    '要记得休息呀',
    '有什么要帮忙的吗？',
    '这题我会！',
  ]

  var STORAGE_POS = 'dsh-remielle-pos'
  var POLL_MS = 1200

  // ── DOM ───────────────────────────────────────────────────────────────
  var style = document.createElement('style')
  style.id = 'dsh-remielle-style'
  style.textContent = [
    '#dsh-remielle{position:fixed;z-index:2147483000;width:' + DW + 'px;height:' + DH + 'px;',
    'cursor:grab;touch-action:none;user-select:none;-webkit-user-select:none;',
    'filter:drop-shadow(0 6px 10px rgba(0,0,0,.25));transition:filter .2s ease}',
    '#dsh-remielle.dragging{cursor:grabbing;filter:drop-shadow(0 10px 16px rgba(0,0,0,.3))}',
    '#dsh-remielle-sprite{width:' + DW + 'px;height:' + DH + 'px;background-image:url(' + API.sheet + ');',
    'background-repeat:no-repeat;background-size:' + COLS * DW + 'px ' + ROWS * DH + 'px;',
    'image-rendering:auto;pointer-events:none}',
    '#dsh-remielle-bubble{position:absolute;left:50%;bottom:calc(100% + 6px);transform:translateX(-50%) scale(.9);',
    'max-width:220px;padding:6px 10px;border-radius:10px;background:rgba(30,30,36,.92);color:#fff;',
    'font:12px/1.45 system-ui,-apple-system,"Segoe UI",sans-serif;white-space:pre-wrap;text-align:center;',
    'opacity:0;pointer-events:none;transition:opacity .18s ease,transform .18s ease;box-shadow:0 6px 18px rgba(0,0,0,.25)}',
    '#dsh-remielle-bubble.show{opacity:1;transform:translateX(-50%) scale(1)}',
  ].join('')
  ;(document.head || document.documentElement).appendChild(style)

  var el = document.createElement('div')
  el.id = 'dsh-remielle'
  el.title = '小蕾米'
  var sprite = document.createElement('div')
  sprite.id = 'dsh-remielle-sprite'
  var bubble = document.createElement('div')
  bubble.id = 'dsh-remielle-bubble'
  el.appendChild(sprite)
  el.appendChild(bubble)
  el.style.right = '18px'
  el.style.bottom = '150px'

  function mount() {
    if (!document.body) {
      setTimeout(mount, 200)
      return
    }
    if (!document.getElementById('dsh-remielle')) document.body.appendChild(el)
  }
  mount()

  // ── 位置：拖动 + 吸附 + 记忆 ─────────────────────────────────────────
  var pos = { left: null, top: null }
  try {
    var saved = localStorage.getItem(STORAGE_POS)
    if (saved) {
      var p = JSON.parse(saved)
      if (p && isFinite(p.left) && isFinite(p.top)) {
        pos.left = p.left
        pos.top = p.top
      }
    }
  } catch (err) {}

  function applyPos() {
    if (pos.left == null) return
    el.style.left = Math.round(pos.left) + 'px'
    el.style.top = Math.round(pos.top) + 'px'
    el.style.right = 'auto'
    el.style.bottom = 'auto'
  }
  applyPos()

  function clamp(v, min, max) {
    return Math.max(min, Math.min(max, v))
  }

  function savePos() {
    try {
      localStorage.setItem(STORAGE_POS, JSON.stringify({ left: pos.left, top: pos.top }))
    } catch (err) {}
  }

  var drag = null
  el.addEventListener('pointerdown', function (e) {
    if (e.button !== 0) return
    var rect = el.getBoundingClientRect()
    drag = { x: e.clientX, y: e.clientY, left: rect.left, top: rect.top, moved: false }
    el.classList.add('dragging')
    try {
      el.setPointerCapture(e.pointerId)
    } catch (err) {}
  })
  el.addEventListener('pointermove', function (e) {
    if (!drag) return
    var dx = e.clientX - drag.x
    var dy = e.clientY - drag.y
    if (!drag.moved && Math.abs(dx) + Math.abs(dy) > 4) drag.moved = true
    if (!drag.moved) return
    pos.left = clamp(drag.left + dx, 4, window.innerWidth - DW - 4)
    pos.top = clamp(drag.top + dy, 4, window.innerHeight - DH - 4)
    applyPos()
  })
  function endDrag(e) {
    if (!drag) return
    var moved = drag.moved
    drag = null
    el.classList.remove('dragging')
    if (moved) {
      // 松手吸附到最近的水平边缘
      var margin = 18
      var mid = window.innerWidth / 2
      pos.left = pos.left + DW / 2 < mid ? margin : window.innerWidth - DW - margin
      applyPos()
      savePos()
      setBase('idle')
    } else {
      onClick(e)
    }
  }
  el.addEventListener('pointerup', endDrag)
  el.addEventListener('pointercancel', function () {
    drag = null
    el.classList.remove('dragging')
  })
  el.addEventListener('dblclick', function (e) {
    e.preventDefault()
    e.stopPropagation()
    playOnce('smug')
    say('诶嘿～')
  })
  el.addEventListener('contextmenu', function (e) {
    e.preventDefault()
  })
  el.addEventListener('pointerenter', function () {
    if (hoverAt && Date.now() - hoverAt < 8000) return
    hoverAt = Date.now()
    playOnce('wave')
  })

  var hoverAt = 0
  var clickAt = 0
  function onClick(e) {
    if (e && e.detail > 1) return
    var now = Date.now()
    if (now - clickAt < 400) return
    clickAt = now
    playOnce('wave')
    say(LINES[Math.floor(Math.random() * LINES.length)])
  }

  var bubbleTimer = null
  function say(text) {
    bubble.textContent = text
    bubble.classList.add('show')
    if (bubbleTimer) clearTimeout(bubbleTimer)
    bubbleTimer = setTimeout(function () {
      bubble.classList.remove('show')
    }, 2600)
  }

  // ── 动画引擎 ─────────────────────────────────────────────────────────
  var base = 'idle'
  var cur = 'idle'
  var frame = 0
  var acc = 0
  var last = performance.now()
  var oneShot = null // { name, at }

  function paint() {
    var a = ANIMS[cur] || ANIMS.idle
    sprite.style.backgroundPosition = '-' + Math.round(frame * DW) + 'px -' + Math.round(a.row * DH) + 'px'
  }

  function setBase(name) {
    base = ANIMS[name] ? name : 'idle'
    if (!oneShot) {
      if (cur !== base) {
        cur = base
        frame = 0
        acc = 0
      }
      paint()
    }
  }

  function playOnce(name) {
    if (!ANIMS[name] || ANIMS[name].loop) return
    oneShot = { name: name }
    cur = name
    frame = 0
    acc = 0
    paint()
  }

  function tick(now) {
    var dt = now - last
    last = now
    var a = ANIMS[cur] || ANIMS.idle
    acc += dt
    var step = 1000 / (a.fps || 8)
    while (acc >= step) {
      acc -= step
      frame += 1
      if (frame >= a.frames) {
        if (a.loop) {
          frame = 0
        } else {
          if (oneShot) oneShot = null
          cur = base
          frame = 0
          acc = 0
          a = ANIMS[cur] || ANIMS.idle
        }
      }
    }
    paint()
    requestAnimationFrame(tick)
  }
  paint()
  requestAnimationFrame(tick)

  // 图集加载失败就当自己不存在，别留个空框
  var probe = new Image()
  probe.onerror = function () {
    try {
      el.remove()
      style.remove()
    } catch (err) {}
  }
  probe.src = API.sheet

  // ── 状态轮询 ─────────────────────────────────────────────────────────
  var lastSeq = -1
  var lastFail = -1
  var lastState = ''

  function applyState(s) {
    if (!s || !s.ok) return
    if (s.inPage === false) {
      // 宿主配置关掉了页面内宠物：自我移除并停止轮询
      try { el.remove(); style.remove() } catch (err) {}
      return
    }
    if (typeof s.failSeq === 'number') {
      if (lastFail >= 0 && s.failSeq > lastFail) {
        playOnce('failed')
        say('出错了…')
      }
      lastFail = s.failSeq
    }
    if (typeof s.doneSeq === 'number' && lastSeq >= 0 && s.doneSeq > lastSeq) {
      playOnce('smug')
      say('完成啦！')
    }
    if (typeof s.doneSeq === 'number') lastSeq = s.doneSeq
    var next = s.state === 'waiting' ? 'waiting' : s.state === 'working' ? 'working' : 'idle'
    if (next !== lastState) {
      lastState = next
      setBase(next)
      if (next === 'waiting') say('在等你哦…')
    }
  }

  function poll() {
    fetch(API.state, { cache: 'no-store', credentials: 'same-origin' })
      .then(function (r) {
        return r.ok ? r.json() : null
      })
      .then(function (s) {
        applyState(s)
      })
      .catch(function () {})
      .then(function () {
        setTimeout(poll, POLL_MS)
      })
  }

  setTimeout(poll, 400)
  setTimeout(function () {
    playOnce('wave')
    say('小蕾米来啦～')
  }, 900)
})()
