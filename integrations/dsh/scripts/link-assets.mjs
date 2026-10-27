// 从仓库自带的 output/xiaolemi 取素材，并生成 DSH 插件需要的文件。
//
//   node scripts/link-assets.mjs            # 复制 pet.json / spritesheet.webp + 切帧
//   node scripts/link-assets.mjs --no-frames # 只复制两个文件
//
// 产物（都在 .gitignore 里，不会进版本库）：
//   assets/pet.json
//   assets/spritesheet.webp
//   desktop/frames/<anim>/<n>.png + desktop/frames/manifest.json
//
// 依赖：Node 22+，Python 3 + Pillow（只切片时用到）。
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { execFileSync } from 'node:child_process'

const here = path.dirname(fileURLToPath(import.meta.url))
const pluginRoot = path.resolve(here, '..')
const repoRoot = path.resolve(pluginRoot, '../..')

const assetSrc = path.join(repoRoot, 'output', 'xiaolemi')
const assetsDst = path.join(pluginRoot, 'assets')
const framesDst = path.join(pluginRoot, 'desktop', 'frames')
const slicer = path.join(repoRoot, 'tools', 'slice-spritesheet.py')

// DSH 侧约定的动作名（顺序与图表行一致；最后两行是 16 向视线，切完要合并）
const ROW_NAMES = [
  'idle', 'runR', 'runL', 'wave', 'smug',
  'failed', 'waiting', 'working', 'review',
  'gaze-a', 'gaze-b',
]

function fail(msg) {
  console.error('✗ ' + msg)
  process.exit(1)
}

function copyAsset(name) {
  const from = path.join(assetSrc, name)
  const to = path.join(assetsDst, name)
  if (!fs.existsSync(from)) fail(`找不到素材：${from}`)
  fs.mkdirSync(assetsDst, { recursive: true })
  fs.copyFileSync(from, to)
  const kb = (fs.statSync(to).size / 1024).toFixed(1)
  console.log(`✓ ${name}  (${kb} KB)`)
}

function pythonCmd() {
  for (const candidate of [process.env.PYTHON, 'python', 'py']) {
    if (!candidate) continue
    try {
      const args = candidate === 'py' ? ['-3', '--version'] : ['--version']
      execFileSync(candidate, args, { stdio: 'ignore' })
      return { cmd: candidate, prefix: candidate === 'py' ? ['-3'] : [] }
    } catch {
      /* try next */
    }
  }
  return null
}

function sliceFrames() {
  if (!fs.existsSync(slicer)) fail(`找不到切帧脚本：${slicer}`)
  const py = pythonCmd()
  if (!py) fail('找不到 python（需要 Python 3 + Pillow）')

  fs.rmSync(framesDst, { recursive: true, force: true })
  execFileSync(
    py.cmd,
    [...py.prefix, slicer, path.join(assetsDst, 'spritesheet.webp'), framesDst, '--names', ROW_NAMES.join(',')],
    { stdio: 'inherit' },
  )

  // 16 向视线：gaze-a(0..7) + gaze-b(8..15) 合并成 gaze/0..15
  const gazeDir = path.join(framesDst, 'gaze')
  fs.mkdirSync(gazeDir, { recursive: true })
  for (const [offset, dir] of [[0, 'gaze-a'], [8, 'gaze-b']]) {
    const from = path.join(framesDst, dir)
    if (!fs.existsSync(from)) continue
    for (const file of fs.readdirSync(from).sort((a, b) => parseInt(a) - parseInt(b))) {
      const index = parseInt(file, 10)
      if (!Number.isFinite(index)) continue
      fs.copyFileSync(path.join(from, file), path.join(gazeDir, String(index + offset) + '.png'))
    }
    fs.rmSync(from, { recursive: true, force: true })
  }
  const gazeCount = fs.readdirSync(gazeDir).length
  console.log(`✓ frames 生成完成（gaze ${gazeCount} 帧）`)
  console.log(`  -> ${path.relative(repoRoot, framesDst)}`)
}

copyAsset('pet.json')
copyAsset('spritesheet.webp')
if (process.argv.includes('--no-frames')) {
  console.log('\n已跳过切帧（--no-frames）。桌面挂件需要 frames/，网页挂件不需要。')
} else {
  sliceFrames()
}
console.log('\n下一步：dsh plugin --profile desktop add "link:' + pluginRoot + '"')
