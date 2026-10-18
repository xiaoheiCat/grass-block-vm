# 第三方组件声明（THIRD PARTY NOTICES）

Grass Block VM 使用并随安装包分发以下第三方组件。各组件著作权归其作者所有。
上游运行时随包携带的许可证文件原样保留；本仓库直接纳入的 spice-html5 许可证原文
位于 `apps/desktop/src/renderer/public/spice-html5/`。本项目自身代码以 GPLv3 发布。

| 组件 | 上游 | 许可证 | 用途与边界 |
| --- | --- | --- | --- |
| QEMU（qemu-system-x86_64 / qemu-img） | https://www.qemu.org/ | GPLv2 | 虚拟化后端。**不做任何魔改**；直接使用官网指向的 Windows 预编译版 |
| OVMF（edk2 固件） | https://github.com/tianocore/edk2 | BSD-2-Clause-Patent | UEFI / Secure Boot 固件（含 secboot 变体） |
| spice-protocol / spice-gtk | https://gitlab.freedesktop.org/spice | LGPL-2.1 | GrassSpiceHelper 依赖（Web 侧做不好的 SPICE 通道） |
| spice-html5（JS） | https://gitlab.freedesktop.org/spice/spice-html5 | LGPL-3.0-or-later | 显示器窗口画面、SPICE 协议与键鼠输入；**作为独立组件引入，不修改其源码** |
| SPICE Windows Guest Tools | https://www.spice-space.org/download/windows/spice-guest-tools/ | 各组件许可证（QEMU-GA: GPLv2, vdagent-win: GPLv2 等） | "客户机帮助程序"，由用户在 Guest 内安装 |
| TAP-Windows6 | https://github.com/OpenVPN/tap-windows6 | GPLv2 | 桥接 / Host-only 网络的虚拟网卡驱动 |
| Electron | https://www.electronjs.org/ | MIT（捆绑 Chromium/Node 许可证见其仓库） | 桌面 UI 壳 |
| React | https://react.dev/ | MIT | 渲染层 |
| Vite / Vitest | https://vite.dev/ | MIT | 构建与测试（仅开发期，不分发） |
| SQLite / Microsoft.Data.Sqlite | https://sqlite.org / https://learn.microsoft.com/dotnet/ephemeral | Public Domain / MIT | 宿主级配置库 |
| .NET 运行时（self-contained 发布） | https://dotnet.microsoft.com/ | MIT | GrassCore 编译目标；self-contained 随包分发 |

> 许可证合规要点：Grass Block VM 以 GPLv3 分发时，对 LGPL 组件（spice-html5、spice-gtk）
> 以独立组件方式引用并保持可替换；如后续修改这些组件并再分发，需按 LGPL 要求提供对应源码。
> 用户数据（.grassvm、镜像、快照）不属于程序组成部分，卸载不清除。
# spice-html5

`apps/desktop/src/renderer/public/spice-html5/` contains the upstream
spice-html5 JavaScript client, licensed under the GNU Lesser General Public
License, version 3 or later. The original notices are retained in the copied
`COPYING` and `COPYING.LESSER` files.
