/**
 * dsh-remielle-pet — 小蕾米桌宠（DSH 版）宿主半区
 *
 * 职责：
 *   1) 向页面注入 /dsh-remielle/client.js（桌面端走 webserver/index-inject，网页端走 webServer.tapIndex）
 *   2) 提供静态资源路由：client.js / pet.json / spritesheet.webp
 *   3) 汇总当前会话活动，暴露 /dsh-remielle/state.json 给客户端做状态映射
 *
 * 设计原则：任何一步失败都只影响挂件本身，绝不阻断宿主启动。
 *   - 注入行用内联 script，自己建 <script src> 并吞掉 onerror（对齐 dsh-whale-widget 的 #154 教训）
 *   - 所有服务读取都走 ctx.get(...) 的可选路径，缺服务时静默降级
 */

import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { spawn, execFileSync } from 'node:child_process'

const PACKAGE_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const ASSETS_DIR = path.join(PACKAGE_ROOT, 'assets')

// 桌面挂件：随宿主启动拉起，宿主退出时杀掉
const DESKTOP_DIR = path.join(PACKAGE_ROOT, 'desktop')
const DESKTOP_EXE = path.join(DESKTOP_DIR, 'remielle-pet.exe')
let desktopChild = null

function startDesktopPet() {
  try {
    if (!fs.existsSync(DESKTOP_EXE)) return
    if (desktopChild && !desktopChild.killed) return
    // 接管：宿主启动时把用户手动开的实例换成受控实例，保证「随 DSH 启停」
    try {
      execFileSync('taskkill', ['/IM', 'remielle-pet.exe', '/F'], { stdio: 'ignore', windowsHide: true })
    } catch (err) {}
    desktopChild = spawn(DESKTOP_EXE, ['--linked'], {
      cwd: DESKTOP_DIR,
      stdio: 'ignore',
      windowsHide: true,
    })
    desktopChild.on('exit', () => { desktopChild = null })
    desktopChild.on('error', () => { desktopChild = null })
  } catch (err) {
    desktopChild = null
  }
}

function stopDesktopPet() {
  try {
    if (desktopChild && !desktopChild.killed) desktopChild.kill()
  } catch (err) {}
  desktopChild = null
}

const PREFIX = '/dsh-remielle'
const CLIENT_SRC = PREFIX + '/client.js'

// 页面注入行：自建 script 元素，路由不在时静默失败，宿主永远不因此起不来
const INJECT_SCRIPT =
  '(function(){try{var d=document.body||document.head||document.documentElement;if(!d)return;' +
  'var s=document.createElement("script");s.src="' + CLIENT_SRC + '";' +
  's.onerror=function(){};d.appendChild(s)}catch(e){}})()'

const STATIC = {
  [CLIENT_SRC]: { file: 'client.js', type: 'text/javascript; charset=utf-8' },
  [PREFIX + '/pet.json']: { file: 'pet.json', type: 'application/json; charset=utf-8' },
  [PREFIX + '/spritesheet.webp']: { file: 'spritesheet.webp', type: 'image/webp' },
  [PREFIX + '/icon.png']: { file: 'icon.png', type: 'image/png' },
}

const cache = new Map()
function assetBytes(file) {
  if (cache.has(file)) return cache.get(file)
  const buf = fs.readFileSync(path.join(ASSETS_DIR, file))
  cache.set(file, buf)
  return buf
}

// ── 会话活动汇总 ───────────────────────────────────────────────────────────
// running: sessionId -> 最近一次活动时间戳
const running = new Map()
let pending = null // { kind: 'question' | 'approval', ts, id }
let doneAt = 0 // 最近一轮结束时间
let doneSeq = 0 // 每完成一轮 +1，客户端据此只庆祝一次
let lastEventAt = 0
let failSeq = 0 // 每失败一轮 +1，客户端据此播一次「失败」动作
let lastFailAt = 0
let lastTurnReason = '' // 记录 turn/end 的原始 reason，排障用
let toolErrorInTurn = false // 本轮出现过工具报错

const IDLE_AFTER_MS = 10 * 60 * 1000 // 长时间无事件视为掉线，避免永远“工作中”
const QUESTION_TOOL = 'ask_user_question'

function callIdOf(event) {
  try {
    const d = (event && event.data) || {}
    const msg = d.message || {}
    const list = Array.isArray(msg.content) ? msg.content : []
    for (const c of list) if (c && (c.toolCallId || c.tool_call_id)) return String(c.toolCallId || c.tool_call_id)
    if (msg.callId || msg.toolCallId || msg.tool_call_id) return String(msg.callId || msg.toolCallId || msg.tool_call_id)
    return String(d.callId || d.call_id || d.toolCallId || d.id || '')
  } catch (err) {
    return ''
  }
}

// 诊断：把最近一轮 turn/end 的形状写到 $DSH_HOME，方便核对失败信号是否可靠
function noteLastTurn(info) {
  try {
    const file = path.join(DSH_HOME, '.dsh-remielle-last-turn.json')
    fs.writeFileSync(file, JSON.stringify(info))
  } catch (err) {}
}

function noteEvent(session, event) {
  try {
    const type = String((event && event.type) || '')
    const sid = String((session && (session.id || session.sessionId)) || 'current')
    const now = Date.now()
    lastEventAt = now

    if (type === 'turn/start') {
      running.set(sid, now)
      pending = null
      toolErrorInTurn = false
      return
    }
    if (type === 'turn/end') {
      running.delete(sid)
      pending = null
      // 失败判定：turn/end 的 reason，或本轮出现过工具报错
      const d = (event && event.data) || {}
      const reason = String(d.reason || d.status || d.outcome || d.error || '')
      lastTurnReason = reason
      const failed = toolErrorInTurn || /error|fail|abort|timeout|block|reject|cancel/i.test(reason)
      noteLastTurn({ reason, failed, keys: Object.keys(d), ts: now })
      toolErrorInTurn = false
      if (failed) {
        failSeq += 1
        lastFailAt = now
      } else {
        doneAt = now
        doneSeq += 1
      }
      return
    }
    if (type === 'tool/call') {
      running.set(sid, now)
      const name = String((event.data && (event.data.name || event.data.toolName)) || '')
      if (name === QUESTION_TOOL) {
        pending = { kind: 'question', id: callIdOf(event) || ('q' + now), ts: now }
      } else if (/approval|permission/i.test(name)) {
        pending = { kind: 'approval', id: callIdOf(event) || ('a' + now), ts: now }
      }
      return
    }
    if (type === 'tool/result') {
      running.set(sid, now)
      const d = (event && event.data) || {}
      if (d.isError === true || d.ok === false || d.error != null) toolErrorInTurn = true
      const cid = callIdOf(event)
      if (pending && (!cid || !pending.id || cid === pending.id)) pending = null
      return
    }
    if (type === 'session/disposed') {
      running.delete(sid)
      if (pending && pending.session === sid) pending = null
      return
    }
    // 其它事件：只要会话在动就刷新时间戳，让“工作中”不会提前熄灭
    if (running.has(sid)) running.set(sid, now)
  } catch (err) {
    /* 状态跟踪永不影响宿主 */
  }
}

function statePayload() {
  const now = Date.now()
  for (const [sid, ts] of running) {
    if (now - ts > IDLE_AFTER_MS) running.delete(sid)
  }
  let state = 'idle'
  if (pending && now - pending.ts < 5 * 60 * 1000) state = 'waiting'
  else if (running.size > 0) state = 'working'
  return {
    ok: true,
    state, // idle | working | waiting
    pending: pending ? pending.kind : null,
    sessions: running.size,
    doneAt,
    doneSeq,
    failSeq,
    lastFailAt,
    lastTurnReason,
    inPage: inPageEnabled(),
    now,
  }
}

function json(res, body) {
  const text = JSON.stringify(body)
  res.writeHead(200, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
    'Content-Length': Buffer.byteLength(text),
  })
  res.end(text)
}

function bytes(res, type, buf) {
  res.writeHead(200, {
    'Content-Type': type,
    'Cache-Control': 'no-store',
    'Content-Length': buf.length,
  })
  res.end(buf)
}

// 配置：config.json（缺省 = 页面内显示 + 桌面显示）
function readConfig() {
  try {
    const file = path.join(PACKAGE_ROOT, 'config.json')
    if (!fs.existsSync(file)) return {}
    return JSON.parse(fs.readFileSync(file, 'utf8')) || {}
  } catch (err) {
    return {}
  }
}

function inPageEnabled() {
  return readConfig().inPage !== false
}

export default {
  name: 'remielle-pet',
  apply(root) {
    const disposers = []
    root.effect(() => () => {
      for (const d of disposers) {
        try {
          d()
        } catch (err) {}
      }
    })

    // ① 桌面端（Electron）唯一生效通道：启动时一次性收集的结构化注入行
    if (inPageEnabled()) try {
      disposers.push(
        root.on('webserver/index-inject', (table) => {
          try {
            if (!Array.isArray(table)) return
            for (const row of table) {
              if (row && row.kind === 'script' && typeof row.text === 'string' && row.text.indexOf(CLIENT_SRC) >= 0) return
            }
            table.push({ kind: 'script', placement: 'body', text: INJECT_SCRIPT })
          } catch (err) {}
        }),
      )
    } catch (err) {}

    // ② 其余逻辑等 webServer 就绪（与鲸鱼插件同样的局部 inject 语义）
    root.inject(['webServer'], (ctx) => {
      const owned = []
      ctx.effect(() => () => {
        for (const d of owned) {
          try {
            d()
          } catch (err) {}
        }
      })

      // 桌面挂件随宿主启停
      try {
        startDesktopPet()
        owned.push(() => stopDesktopPet())
      } catch (err) {}

      // 网页端：直接改写 index.html（config.inPage = false 时不注入）
      if (inPageEnabled()) try {
        owned.push(
          ctx.webServer.tapIndex((html) => {
            if (typeof html !== 'string' || html.indexOf(CLIENT_SRC) >= 0) return html
            const tag = '<script defer src="' + CLIENT_SRC + '"></script>'
            return html.indexOf('</body>') >= 0 ? html.replace('</body>', tag + '</body>') : html + tag
          }),
        )
      } catch (err) {}

      // 静态资源 + 状态接口
      for (const [routePath, spec] of Object.entries(STATIC)) {
        owned.push(
          ctx.webServer.register({
            kind: 'exact',
            path: routePath,
            handler: (req, res) => {
              try {
                bytes(res, spec.type, assetBytes(spec.file))
              } catch (err) {
                try {
                  res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' })
                  res.end('not found')
                } catch (e) {}
              }
            },
          }),
        )
      }
      owned.push(
        ctx.webServer.register({
          kind: 'exact',
          path: PREFIX + '/state.json',
          handler: (req, res) => {
            try {
              json(res, statePayload())
            } catch (err) {
              try {
                res.writeHead(500)
                res.end('')
              } catch (e) {}
            }
          },
        }),
      )

      // 会话事件 → 桌宠状态
      try {
        owned.push(ctx.on('session/event', (session, event) => noteEvent(session, event)))
      } catch (err) {}
    })
  },
}
