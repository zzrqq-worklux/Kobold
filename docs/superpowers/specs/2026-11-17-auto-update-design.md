# Kobold 自动更新设计（2026-11-17）

## 目标

已安装用户不再需要手动下载 zip：应用自己发现新版本、自己下载并校验、在合适的时机替换并重启；
任何一步失败都回滚到旧版本，且绝不影响正在运行的实例。

## 为什么不是「提醒 + 点一下下载」

C 档的要求是真正的 updater。它在 Windows 上有两个无法回避的约束：

1. **运行中的 exe 不能覆盖自己。** 必须有一个外部进程在应用退出后做替换。
2. **安装目录可能不可写**（Program Files）或根本不是安装版（开发目录）。

所以设计成「应用只负责发现、下载、暂存；一个短命的 PowerShell 脚本负责等待退出、备份、替换、重启、回滚」。

## 组成

```
启动 30s 延迟 ──> UpdateService.CheckAsync ──> GET https://api.merrickzq.work/downloads/kobold/latest.json
                        │
                        ├─ 版本不比本地新 / 用户跳过 / 24h 内检查过 ──> 停止
                        │
                        ▼
                  UpdateDownloader：下载到 %TEMP%，边下边算 SHA-256，校验 size
                        │
                        ▼
                  解压到 %TEMP%\Kobold-update\<version>\stage，校验 Kobold.exe 的 FileVersion == manifest.version
                        │
                        ▼
                  状态 Ready（托盘菜单出现「有更新 2.0.2 • 重启生效」）
                        │
   用户点「立即重启更新」 或 用户正常退出应用（自动安装开）
                        │
                        ▼
   UpdateApplier：分离进程 powershell -File tools\apply-update.ps1 -ProcessId <pid> ...，应用随即退出
                        │
                        ▼
   apply-update.ps1：等待退出 → 备份当前安装 → 复制 stage → 启动新 exe → 15s 内活着=成功
                                            └─ 失败：还原备份，启动旧 exe，写日志
```

## 关键决定与理由

| 决定 | 理由 | 代价 |
|---|---|---|
| 默认「退出时自动安装」＝开，「检查」＝开 | 用户不被打断，又能真正完成升级 | 重启发生在用户退出时；用户点「立即重启」可提前 |
| 只在「已安装」状态下自更新 | 开发目录里的 `bin\Debug` 被覆盖会毁掉开发环境 | 未安装时只提示并打开下载页 |
| 替换交给 `tools\apply-update.ps1` | 运行中 exe 无法自替换；PowerShell 处处可用，无需额外二进制 | 多一个脚本要随包分发并进安装目录 |
| 信任模型 = HTTPS + 主机名固定 + manifest 里的 sha256 | 没有代码签名基建，sha 能挡住损坏与中间人替换，但不能挡被攻破的服务器 | 服务器被攻破即可推送任意 exe；文档里明说 |
| 失败一律回滚 + 只留一份备份 | 半截更新是最坏结果 | 多占一份安装体积 |

## 安全边界

- manifest URL 固定为 `https://api.merrickzq.work/downloads/kobold/latest.json`；解析出的下载地址必须是 https 且同主机，否则拒绝。
- 下载大小上限 200 MB；SHA-256 必须与 manifest 完全一致，否则删除临时文件。
- 解压时校验每个 entry 的规范化路径仍在 stage 目录内（zip slip）。
- 不解压后执行任何来自包里的程序，只复制到安装目录并启动 `Kobold.exe`。
- 不发任何机器标识，只发 `User-Agent: Kobold/<version>`。

## 失败模式

| 场景 | 行为 |
|---|---|
| 没网 / 超时 / 404 | 静默，状态保留，24 小时后再试 |
| sha256 不符 / zip 损坏 / 缺 Kobold.exe / 版本不符 | 删除临时文件，状态 Failed，设置页可看原因，不上报 |
| 安装目录不可写 | 不下载，状态 Failed(只读)，回退为「打开下载页」 |
| 替换时文件被占 | 5 次 × 1 s 重试，仍失败则回滚 |
| 新 exe 启动后 15 s 内退出 | 脚本还原备份并启动旧版 |

## 数据与配置

`AppConfig` 新增（全部有默认值，旧配置无需迁移）：

- `UpdateCheckEnabled`（默认 true）
- `UpdateAutoInstall`（默认 true，语义：退出时自动安装）
- `SkippedUpdateVersion`（默认空）
- `LastUpdateCheckUtc`（默认空，ISO-8601）

## 测试策略

- 纯逻辑（manifest 解析/校验、版本比较、跳过与间隔策略、路径规范化、zip entry 校验、安装目录判定）进 `tests/UpdateCheck`，加入 `Run-Checks -Group ci`。
- 下载与解压用 `HttpListener` + 临时 zip 做本机闭环测试，不访问外网。
- `apply-update.ps1` 提供 `-DryRun`，在虚拟机里用假安装目录跑一遍：备份、复制、回滚三条路径都要可断言。
- 端到端：在虚拟机里造一个 2.0.2 的包，走完整 downloader → stage → apply 链路，确认版本真的变了且失败路径能回滚。
