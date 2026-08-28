# Grass Block VM — 架构与模块职责

本文件把《Grass Block VM 1.0 产品与技术实施计划》的架构映射到本仓库的实际模块。
两份源文档位于 `docs/`（产品与技术实施计划、完整对话记录），是所有决策的最终依据。

## 总体架构

```
Grass Block VM.exe (Electron / React)
    │ Windows Named Pipe + JSON-RPC（帧：4 字节小端长度 + JSON）
    ▼
GrassCore.exe (C# / .NET, self-contained, 用户级进程, 按需启动)
    ├─ VM 生命周期 / 配置 / Library（.grassvm 唯一写入者）
    ├─ QMP / QGA（QEMU Named Pipe）
    ├─ qemu-img 编排（事务式 .grass-tmp）
    ├─ 快照 / 克隆 / 导入导出 / 网络资源
    └─ 持久化 / 恢复 / 升级保护
          │
          ├── qemu-system-x86_64.exe（每台 VM 一个进程；未魔改的官方 QEMU）
          │      ├─ QMP: \\.\pipe\grassvm-qmp-<sessionId>
          │      └─ SPICE: 127.0.0.1 自动端口（query-spice 查询）
          └── GrassSpiceHelper.exe（每台运行 VM 一个；Web 做不好的 SPICE 通道）
```

## 目录与模块

### apps/core（GrassCore）

| 模块 | 文件 | 职责（对应计划章节） |
| --- | --- | --- |
| .grassvm 包 | `GrassVm/GrassVmPackage.cs` | 固定目录结构；发现≠已验证；完整性检查；未知文件保留（§13） |
| 原子写 | `GrassVm/AtomicFile.cs` | 临时文件→校验→原子替换；vm.lock 语义（§14） |
| 路径规范 | `GrassVm/PathPolicy.cs` | 包内相对/包外绝对；失效重定位 B+自动迁移（§13.2） |
| 配置模型 | `Config/VmConfiguration.cs` | "虚拟电脑"模型（非 QEMU CLI）；设备多态；热插拔仅 CD（§21） |
| 设备命名 | `Config/DeviceNamer.cs` | 磁盘 #1 / 网络 #2 稳定编号，永不回收（§7.3） |
| 迁移链 | `Config/ConfigStore.cs` | schemaVersion 逐级向前；升级前备份；失败回滚（§14.2） |
| 非关键状态 | `Config/VmState.cs` | state.json（可随 VM 移动）；运行期 secret 只存内存（§21） |
| OS Profile | `Profiles/OsProfile.cs` | 内置 Profile；Windows=SATA/e1000/UEFI+TPM；Linux=VirtIO；legacy=IDE+BIOS（§12） |
| QEMU 命令 | `Qemu/QemuCommandBuilder.cs` | 抽象模型→QEMU 参数；-accel whpx 硬性；QMP Named Pipe；SPICE 本机（§11） |
| 磁盘事务 | `Qemu/TransactionalDiskOps.cs` | qemu-img 编排；.grass-tmp；取消/崩溃不污染原文件（§15） |
| 启动预检 | `Qemu/TransactionalDiskOps.cs`（StartupPreflight） | 资源存在性/vm.lock/显示设备（§22.1） |
| WHPX/升级保护/脱敏 | `Qemu/WhpxCapability.cs` | 三层 WHPX 预检；QEMU major 升级保护快照；日志脱敏（§17/§22.9） |
| 快照 | `Snapshots/SnapshotTree.cs` | 树、非叶删除重绑、链接克隆依赖、恢复警告、升级保护清理（§16） |
| QMP 客户端 | `Qemu/QmpClient.cs` | JSON 行协议 + 传输抽象（Windows Named Pipe / TCP / 内存）；ACPI 关机、quit、挂起 migrate file:、恢复 -incoming、CD 热插拔（§22） |
| 快照落盘 | `Rpc/SnapshotService.cs` | snapshots/&lt;uuid&gt; 布局；完整 config 副本；包外磁盘不进链（§16） |
| 克隆 | `Clone/CloneService.cs` | 完整克隆（独立副本/扁盘化/无快照历史）；链接克隆（必须基于快照/cloneInfo 同目录相对引用）（§16.5） |
| 导入导出 | `ExportImport/GrassVmZip.cs`、`ExportImport/OvfImporter.cs`、`ExportImport/OvfExporter.cs` | 完整档案 zip（关机前置）；OVF/OVA 导入映射（Raw/阻止/仍然导入/空间预估）；OVF 导出当前状态（§15） |
| 会话/接管 | `Rpc/RuntimeSession.cs` | runtime/session.json（包内）；Core 崩溃无损重接管（§6.3） |
| 宿主库 | `Library/HostDb.cs` | SQLite：Library Root、自动启动（串行/10s/顺序）、Host-only 网络、索引缓存（§18/§19） |
| API 表面 | `Rpc/GrassCoreService.cs`、`Rpc/JsonRpc.cs`、`Program.cs` | UI↔Core 全部写操作；Named Pipe（Win）/stdio（开发机） |

### apps/desktop（Electron UI）

| 模块 | 职责 |
| --- | --- |
| `src/main/index.ts` | Library 主窗口 + 显示器窗口；SPICE WebSocket↔TCP 桥；UI 退出不杀 Core |
| `src/main/core-bridge.ts` | GrassCore 按需拉起与 JSON-RPC 客户端（Windows Named Pipe 复用） |
| `src/main/ws-lite.ts` | 本机 WebSocket 桥（字节直通，无协议转换——参考 electerm，不改 spice-client） |
| `src/preload.ts` | contextBridge：渲染层唯一能力入口（QEMU 不可见） |
| `src/renderer/library` | 卡片 Library、创建向导（四步）、设备化设置、强制关机二次确认 |
| `src/renderer/display` | 独立显示器窗口：工具栏/全屏/Ctrl+Alt+Del/关闭四选项/帮助程序安静原则 |

### installer

NSIS 骨架：一开始整体提权一次装齐（UI + Core + Helper + QEMU + TAP 驱动）；有 VM 运行禁止更新；整包更新。

## 与实施计划的阶段映射（§23）

| 阶段 | 状态 |
| --- | --- |
| P0 工程底座 | ✅ monorepo / 许可证 / CI / 测试基建 |
| P1 GrassCore 核心域 | ✅ 配置迁移、原子写、vm.lock、路径规范（61 个单测覆盖） |
| P2 磁盘/QEMU 命令 | ✅ qemu-img 事务、Profile→命令构建、启动预检（Windows 实机验证待做） |
| P3 UI | ◐ Library/向导/设置/显示器骨架就绪；与 Core 真实数据流待 Windows 实机联调 |
| P4 快照/克隆 | ✅ 树模型/落盘/删除重绑/链接克隆依赖/恢复回滚/完整克隆/升级保护快照；QMP 在线快照与 overlay 重写在实机阶段落地 |
| P4b 电源/挂起 | ✅ QmpClient（greeting→capabilities→命令/事件；Windows Named Pipe/TCP/内存传输）+ ACPI 关机/强制退出/挂起（stop→migrate file:→quit）/-incoming 恢复 + 宿主指纹校验；实机联调待做 |
| P5 网络 | ◐ 宿主级网络资源模型 + TAP 命令生成；驱动安装/实测在 Windows 阶段 |
| P6 导入导出 | ✅ .grassvm.zip 完整档案（关机前置/痕迹排除/zip-slip 防护）、OVF/OVA 导入（Raw 设备/阻止与仍然导入/空间预估）、OVF/OVA 导出（当前状态/VMDK 转换）；OVA 打包实测在 Windows 阶段 |
| P7 打包发布 | ◐ NSIS 骨架；签名/驱动细节在 Windows 构建机完成 |
| P8 抛光 | ☐ |

## 已知的 Windows 实机依赖（本仓库以源码就绪 + 文档标注方式处理）

开发环境为 macOS 时，以下链路只能源码就绪、无法本机验证：

1. WHPX 检测调用 Win32 WMI（`WhpxCapability.CheckWindows`，`[SupportedOSPlatform("windows")]`）。
2. QMP 的 Named Pipe 通道（QEMU `-chardev pipe,name=\\.\pipe\...`）。
3. TAP-Windows6 驱动安装与桥接/Host-only 实测。
4. NSIS 安装器编译与升级-驱动替换链路。
5. spice-client 在显示器窗口的真实画面接入（桥已就绪）。

这些是计划 §23 中 P3-P7 的实机验证项，不阻塞核心域逻辑的正确性验证。
