# Grass Block VM

**表面简单，底层强大。** *Simple on the surface. Powerful underneath.*

Grass Block VM 是一个面向 Windows 10 22H2+（x64）的消费级桌面虚拟机管理器。
QEMU 是基岩层（Bedrock）——复杂、庞大、重要，但用户永远不需要看见它；
用户接触的是地表的"草方块"——简单、熟悉、明亮、快速的交互。

```
Grass Block VM.exe  (Electron / React / TypeScript)
    │  Windows Named Pipe + JSON-RPC
    ▼
GrassCore.exe  (C# / .NET, self-contained, 按需启动的用户级进程)
    ├─ VM 生命周期 / 配置 / Library（.grassvm 唯一写入者）
    ├─ QMP / QGA（QEMU Named Pipe）
    ├─ qemu-img 编排（事务式 .grass-tmp）
    ├─ 快照 / 克隆 / 导入导出 / 网络资源
    └── qemu-system-x86_64.exe   （每台 VM 一个进程，未魔改的标准 QEMU）
         └─ GrassSpiceHelper.exe （每台运行 VM 一个，仅补足 Web 做不好的 SPICE 通道）
```

## 产品哲学（冻结决策）

| 原则 | 落地含义 |
| --- | --- |
| QEMU 应当不可见 | 不暴露 machine、VirtIO 型号、CPU flags、QMP、TCG、自定义 QEMU CLI |
| 默认帮用户做对 | OS Profile 自动选择固件、设备型号、TPM/Secure Boot、推荐资源 |
| 提醒风险，不替用户做主 | 高风险删除、破坏链接克隆等场景说明后果，但不强行禁止 |
| 界面展示必须等于当前事实 | 不做"待应用修改"；运行中不可修改的设备直接锁定 |
| 正常状态保持安静 | 客户机帮助程序正常时不显示任何提示 |
| 文件系统是真相 | Library Root 是事实来源；`.grassvm` 包本身就是 VM 身份（VM 无 UUID） |
| 可靠性优先于复杂自动化 | 保守修复、事务式 qemu-img 操作、手动解除残留 vm.lock |

## 仓库结构

```
grass-block-vm/
├─ apps/
│  ├─ desktop/          # Electron UI（React + TypeScript + Vite）
│  │  ├─ src/main/      # 主进程：GrassCore 启动、IPC、SPICE WebSocket 桥
│  │  ├─ src/preload/   # contextBridge
│  │  └─ src/renderer/  # Library 卡片、创建向导、设备化设置、显示器窗口
│  └─ core/             # GrassCore（C# / .NET）+ 单元测试
├─ installer/           # NSIS 安装器骨架
├─ docs/                # 设计文档（含两份 PDF 源文档）
├─ LICENSE              # GPLv3
└─ THIRD_PARTY_NOTICES.md
```

## 开发

需要：Node.js 20+、pnpm、.NET SDK（开发机；最终发布 self-contained，用户零依赖）。

```bash
pnpm install
pnpm build          # 全部构建（UI typecheck+build、Core build）
pnpm test           # 全部测试（Core: dotnet test；UI: vitest）
pnpm dev            # 一键开发启动（Electron + Vite）
pnpm core:test      # 仅 Core 测试
```

`.NET SDK` 如未安装，可装到仓库本地（不入库）：

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
./dotnet-install.sh --channel LTS --install-dir "$PWD/.dotnet"
export PATH="$PWD/.dotnet:$PATH"
# dotnet CLI 写 ~/.dotnet 受限时：
export DOTNET_CLI_HOME="$PWD/.dotnet-home" NUGET_PACKAGES="$PWD/.nuget"
```

开发机（非 Windows）调试 GrassCore：`dotnet run --project apps/core/src/GrassCore` 走 stdio JSON-RPC（4 字节小端长度前缀 + JSON），Debug 构建可用 `GRASSCORE_QEMU_DIR` / `GRASSCORE_OVMF_DIR` / `GRASSCORE_QEMU_MAJOR` 环境变量指向假 QEMU 目录；Release 构建必须显式追加 `--dev-runtime` 才允许覆盖。正式安装版不接受这些环境变量，只从安装包内固定目录加载运行时。

## 平台边界（1.0）

- 宿主：Windows 10 22H2+ x64，仅此一种；不支持 Windows ARM64 / Linux / macOS 宿主。
- Guest：x86/x64；QEMU 能合理运行的系统原则上可创建，仅对有 Profile/经过验证的系统提供"推荐/已验证"体验。
- 每台 VM 必须有显示设备（不支持 Headless）；只使用硬件虚拟化（WHPX），初始化失败即阻止启动，绝不静默回退 TCG。
- 1.0 不做：高级 QEMU 参数、3D 加速开关、多显示器、暂停（Pause）、运行中热插拔磁盘/网卡、模板、加密、插件、CLI、搜索、无人值守安装。

## 许可证

- 本项目代码：**GPLv3**（不允许闭源衍生分发）。
- 随包分发的 QEMU / SPICE / TAP-Windows6 等第三方组件遵守各自许可证，详见 `THIRD_PARTY_NOTICES.md`。

> 本仓库目前是按照《Grass Block VM 1.0 产品与技术实施计划》实施的工程骨架与核心实现；
> Windows 专属链路（WHPX 检测、Named Pipe QMP、TAP、NSIS 打包）以源码 + 文档形式就绪，需在 Windows 机器上完成实机验证。
