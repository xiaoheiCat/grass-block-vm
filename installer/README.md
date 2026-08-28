# Grass Block VM — NSIS 安装器骨架

安装策略（冻结决策）：

- **只提供安装版**，无绿色版/便携版。
- 安装器一开始就整体提权（UAC），一次性安装全部组件：
  - Grass Block VM 主程序（Electron UI）
  - GrassCore.exe（self-contained，用户机器零 .NET 依赖）
  - GrassSpiceHelper.exe（SPICE 能力补足，按需每 VM 一个进程）
  - QEMU Runtime（官网指向的 Windows 预编译版，不魔改）
  - TAP-Windows6 驱动（Grass Block VM Virtual Ethernet Adapter，桥接/Host-only 用）
  - spice-client 前端资源
- **始终整包更新**：每个版本 = 一套确定版本的 Core + QEMU + Helper + 驱动，不做组件级版本组合。
- **有任何 VM 运行（或挂起恢复中）时禁止安装更新**。
- 卸载需清理驱动与虚拟网卡；提示 Library 中的 `.grassvm` 数据默认保留（用户可勾选删除）。

## main.nsi 骨架

参见 `main.nsi`（待 Windows CI 上配齐签名与驱动安装细节后启用）。

## 构建产物布局（安装后）

```
C:\Program Files\Grass Block VM\
├─ Grass Block VM.exe
├─ resources\
│  ├─ app.asar
│  ├─ GrassCore\GrassCore.exe
│  ├─ GrassSpiceHelper\GrassSpiceHelper.exe
│  ├─ QEMU\            # qemu-system-x86_64.exe / qemu-img.exe / DLLs
│  └─ firmware\        # OVMF_CODE.fd / OVMF_CODE.secboot.fd / OVMF_VARS.fd
└─ LICENSE / THIRD_PARTY_NOTICES
```

用户数据：

- `%LOCALAPPDATA%\GrassBlockVM\grass.db`（SQLite：Library Root、偏好、自动启动、Host-only 网络、索引缓存）
- 默认 Library Root：`%USERPROFILE%\Documents\Grass Block VM`
