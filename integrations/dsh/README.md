# DSH 版小蕾米（DeepSeek Harness 集成）

> 第三方适配，与米哈游 / HoYoverse / OpenAI 无关，也不代表本仓库官方支持。
> **本目录只放代码**；图集复用仓库里已有的 `output/xiaolemi/`，不另存一份。

DSH（DeepSeek Harness）是插件化的 AI 工作台。这个集成把 `output/xiaolemi/` 的图集接进 DSH，
提供两半：

- **页面挂件**：DSH 界面右下角的小蕾米，跟随会话状态换动作；
- **桌面挂件**：透明置顶窗口（C#/WPF，不需要 Electron），可拖到桌面任意位置。

## 目录

```
integrations/dsh/
├─ lib/index.js             # 宿主半区：注入、路由、状态汇总、拉起/关闭桌面挂件
├─ assets/client.js         # 页面半区：切帧、拖拽、气泡、视线
├─ cordis.patch.yml         # DSH bundle patch（插入插件行）
├─ package.json
├─ scripts/link-assets.mjs  # 从 ../../output/xiaolemi 取素材 + 切帧
└─ desktop/
   ├─ Pet.cs                # 桌面挂件源码（WPF）
   ├─ build.ps1             # 用系统自带 csc 编译
   └─ start-pet.vbs         # 无黑框启动（手动模式）
```

## 安装

前置：DSH 0.2+，Node 22+，.NET Framework 4.x（Win10/11 自带），Python 3 + Pillow（切帧用）。

```powershell
cd integrations/dsh

# 1) 取素材 + 切帧（spritesheet.webp -> desktop/frames/*.png）
node scripts/link-assets.mjs

# 2) 编译桌面挂件（可选；只要页面挂件可跳过）
powershell -ExecutionPolicy Bypass -File desktop/build.ps1

# 3) 装进 DSH
dsh plugin --profile desktop add "link:$PWD"
# 然后重启 DeepSeek Harness
```

只想在浏览器里用（`dsh web`）就把 `--profile desktop` 换成 `--profile web`。

卸载：

```powershell
dsh plugin --profile desktop remove dsh-remielle-pet
```

## 配置

`config.json`（与 `package.json` 同级，需自行创建）：

```json
{ "inPage": false }
```

- 缺省 `true`：页面挂件 + 桌面窗口都显示；
- `false`：只显示桌面窗口，页面里连脚本都不注入；
- 改完需重启 DSH。

桌面窗口的尺寸/位置记在 `desktop/pet-state.txt`，日志在 `desktop/pet.log`。

## 动作映射

图集 8 列 × 11 行、每格 192×208：

| DSH 状态 | 动作 | 行 | 帧数 |
|---|---|---|---|
| 空闲 | idle | 0 | 7 |
| 工作（turn/start、tool/call） | running | 7 | 6 |
| 等待输入 / 审批 | waiting | 6 | 6 |
| 一轮完成 | jumping（播一次 840 ms） | 4 | 5 |
| 一轮失败 | failed（连播 ~3 s） | 5 | 8 |
| 点击 / 悬停 | waving（700 ms） | 3 | 4 |
| 拖动 | running-left / running-right | 2 / 1 | 8 / 8 |
| 指针靠近 | 16 向视线 | 9–10 | 16 |

16 向视线的取帧公式（实测校准）：`index = round(((90 - atan2(-dy, dx)) mod 360) / 22.5) mod 16`，
其中 `0° = 正对镜头、90° = 朝右、180° = 背对、270° = 朝左`。

## 行为细节

- 页面挂件：左键拖动（松手吸附边缘）、单击招手、双击得意、悬停招手、位置存 localStorage；
- 桌面挂件：透明置顶、拖拽（跟方向播跑动）、吸附/记忆、右键菜单（尺寸/视线/穿透/归位/退出）、
  托盘图标、全局热键 `Ctrl+Alt+R`（被占用时自动顺延）切换鼠标穿透；
- 启停：DSH 启动时插件拉起桌面窗口，DSH 退出时关闭；`--linked` 模式下失联 60 秒自退，
  手动从 `start-pet.vbs` 启动则是独立模式（不随 DSH 退出）；
- 单实例：全局只允许一只（命名 Mutex），重复启动会被忽略。

## 素材与授权

- 图集、`pet.json`、角色形象：作者 **HanaAyane / 小蕾米**，
  来源 <https://github.com/HanaAyane/remielle-codex-pet>，
  基于《绝区零》公开素材经 AI 辅助重绘与人工整理，**仅限非商业同人交流**，
  详见仓库根的 [`NOTICE.md`](../../NOTICE.md) 与 [`ASSET-USAGE.md`](../../ASSET-USAGE.md)。
- 本目录内的代码（`lib/`、`assets/client.js`、`desktop/Pet.cs`、脚本）由 **Focaster** 编写，
  **保留所有权利**，随本分支分享，未授予再分发或商用许可。
- 本目录**没有**、也不应添加覆盖整个仓库的 `LICENSE`。
