using System.Formats.Tar;
using System.Xml.Linq;
using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Qemu;
using GrassCore.Rpc;

namespace GrassCore.ExportImport;

/// <summary>
/// OVF/OVA 导入导出（§15.1/§15.2 冻结决策）：
/// - 尽量忠实保留原虚拟硬件；能映射到原生设备的转换；无法安全编辑但可传给 QEMU 的保留为
///   Raw/兼容设备（只读展示原始参数，只能删除重新添加）；完全无法支持的先阻止导入，
///   用户选择"仍然导入"后允许导入但标记不可用。
/// - VMDK 等源磁盘导入时统一转换为 QCOW2（事务式）；转换前估算空间，不足直接给出所需 GB 数。
/// </summary>
public sealed class OvfImporter(TransactionalDiskOps diskOps, string? ovmfVarsTemplate = null)
{
    public sealed record ImportPlan(
        VmConfiguration Config,
        IReadOnlyList<DiskImport> Disks,
        /// <summary>可变：执行阶段（NVRAM 模板缺失等）还会补充警告。</summary>
        List<string> Warnings,
        /// <summary>完全无法支持、默认会阻止导入的设备（用户选"仍然导入"后以不可用状态保留）。</summary>
        IReadOnlyList<string> UnsupportedDevices)
    {
        public bool BlocksImport => UnsupportedDevices.Count > 0;
    }

    public sealed record DiskImport(string SourceFile, long VirtualSizeBytes, string TargetRelativePath,
        /// <summary>信封声明的源格式（DiskSection/@format 原文）。能映射成 qemu-img
        /// 格式名时转换显式 -f 锁定，绝不靠探测——声东击西的镜像（说是 VMDK
        /// 实为带宿主 backing 的 qcow2）探测不出破绽。</summary>
        string? SourceFormat = null);

    // OVF CIM 资源类型（常用子集）
    private const int ResProcessor = 3;
    private const int ResMemory = 4;
    private const int ResEthernet = 10;
    private const int ResCdDvd = 15;
    private const int ResDiskDrive = 16;
    private const int ResHardDisk = 17;
    private const int ResUsbController = 21;
    private const int ResVideo = 24;

    /// <summary>分析 .ovf（或多文件目录）；OVA 先解开 tar 再复用本方法。</summary>
    public ImportPlan PlanFromOvf(XDocument ovfDoc, string baseDir)
    {
        var ns = OvfNames(ovfDoc);
        var env = ovfDoc.Root ?? throw new GrassCoreException("OVF 文档为空。");
        var diskSection = env.Element(ns + "DiskSection");
        // 重复的 id/diskId（手写档案/坏掉的导出工具）会让 ToDictionary 裸抛
        // ArgumentException——首个生效的宽容映射，语义与"未知 id 引用解析
        // 失败→警告"一致
        var files = env.Element(ns + "References")?.Elements(ns + "File")
            .GroupBy(f => (string)f.Attribute(ns + "id")!)
            .ToDictionary(g => g.Key, g => (string)g.First().Attribute(ns + "href")!, StringComparer.Ordinal)
            ?? new Dictionary<string, string>();
        // diskId → (fileRef, virtualSize, format)：capacity × capacityAllocationUnits（默认字节）
        var disks = diskSection?.Elements(ns + "Disk")
            .GroupBy(d => (string?)d.Attribute(ns + "diskId") ?? "")
            .ToDictionary(
                g => g.Key,
                g => (
                    FileRef: (string?)g.First().Attribute(ns + "fileRef") ?? "",
                    Capacity: ParseCapacity(
                        (string?)g.First().Attribute(ns + "capacity") ?? "0",
                        (string?)g.First().Attribute(ns + "capacityAllocationUnits")),
                    Format: (string?)g.First().Attribute(ns + "format")),
                StringComparer.Ordinal) ?? new Dictionary<string, (string FileRef, long Capacity, string? Format)>();

        var config = new VmConfiguration { Name = "Imported VM", OsProfileId = "other" };
        var diskImports = new List<DiskImport>();
        var warnings = new List<string>();
        var unsupported = new List<string>();
        int order = 0;
        var scsiIdx = 0; // SCSI 控制器 id 计数（id=scsi0、scsi1…不重复）
        var usedDiskTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 直接子元素优先（我方导出的形态），兜底再往深处找一层：DMTF 规范的
        // 多 VM 形态是 Envelope→VirtualSystemCollection→VirtualSystem×N——
        // 只看直接子元素会拿到 null，Item 循环就退回【整棵树】把所有 VM 的
        // 盘/设备拼进一台机器，"仅导入第一个"的警告也随之变成谎言
        var vs = env.Element(ns + "VirtualSystem")
                   ?? env.Elements().FirstOrDefault(e => e.Name.LocalName == "VirtualSystem")
                   ?? env.Descendants().FirstOrDefault(e => e.Name.LocalName == "VirtualSystem");
        if (vs is not null)
        {
            var nameEl = vs.Element(ns + "Name") ?? vs.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            if (nameEl is not null) config.Name = nameEl.Value;
        }
        // OVF 允许 VirtualSystemCollection（一台信封多个 VM）。全部混读会拼出
        // 一台"科学怪人"：两台 VM 的盘挂上同一机器、CPU/内存看谁解析在最后、
        // 第二台的配置静默丢失。只导入第一个 VirtualSystem，其余明确警告
        var vsCount = env.Descendants().Count(e => e.Name.LocalName == "VirtualSystem");
        if (vsCount > 1)
            warnings.Add($"这份档案包含 {vsCount} 个虚拟系统，仅导入第一个（其余系统的配置与磁盘被忽略）。");
        // 我方导出的声卡/USB 启停扩展（gvm:Usb、gvm:Audio）。缺失 = 第三方信封，
        // 后续按 Profile 默认补齐；在场则如实还原——用户关掉的设备不能被
        // "Profile 默认是开"悄悄打开
        bool? GvmFlag(string localName)
        {
            var el = vs?.Elements().FirstOrDefault(e => e.Name.LocalName == localName
                && e.Name.Namespace == OvfExporter.GvmNs);
            var v = (string?)el?.Attribute("enabled");
            return v is null ? (bool?)null : string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
        var GvmUsbEnabled = GvmFlag("Usb");
        var GvmAudioEnabled = GvmFlag("Audio");

        // Item 只认第一个 VirtualSystem 的（多系统信封在上面已警告；无系统
        // 元素的信封退回整棵树——兼容只有磁盘段的极简形态）
        foreach (var item in (vs ?? env).Descendants().Where(e => e.Name.LocalName == "Item"))
        {
            var typeValue = item.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceType")?.ParseNumber();
            var type = typeValue.HasValue ? (int)typeValue.Value : -1;
            var caption = item.Elements().FirstOrDefault(e => e.Name.LocalName == "Caption")?.Value ?? "";
            var description = item.Elements().FirstOrDefault(e => e.Name.LocalName == "Description")?.Value ?? "";

            // 我方导出的 Raw 兼容设备扩展（gvm:QemuArgs，每行一个参数）：优先于
            // 资源类型判定——静默丢弃 = "导出→导入"往返丢设备。
            // 信任判定不看出处标记（标记写在被审文件里，伪造无成本）：
            // 只认【内容】——白名单只放行 -device <型号>（不带宿主文件引用）。
            // 白名单外的任何参数（含我们自己在旧版本导出的）→ 占位不可用 + 用户确认
            var qemuArgsEl = item.Elements().FirstOrDefault(e => e.Name.LocalName == "QemuArgs"
                && e.Name.Namespace == OvfExporter.GvmNs);
            if (qemuArgsEl is not null)
            {
                var args = qemuArgsEl.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(a => a.TrimEnd('\r')).ToList();
                if (args.Count > 0)
                {
                    // 往返保真：导出侧带出的"占位（不可用）"标记
                    var exportedUnsupported =
                        string.Equals((string?)qemuArgsEl.Attribute("unsupported"), "true", StringComparison.OrdinalIgnoreCase);
                    var safe = AreSafeCompatArgs(args);
                    var label = caption.Length > 0 ? caption : "兼容设备";
                    if (!safe)
                    {
                        // 同一段文字既进确认列表也当设备名：ExecuteAsync 补占位时按
                        // 名字去重，不会出现同一设备两条
                        label = $"{label}（含不支持的原始参数，保留为占位且不生效）";
                        unsupported.Add(label);
                    }
                    config.Devices.Add(new RawDevice
                    {
                        Label = label,
                        Arguments = args,
                        Unsupported = exportedUnsupported || !safe,
                        CreatedOrder = ++order,
                    });
                    continue;
                }
            }

            switch (type)
            {
                case ResProcessor:
                    // 与内存同一套钳制：OVF 数量没有可信上限，恶意/单位错误的包
                    // 不该产出 -smp <越界值>（启动即崩的原始 QEMU 报错）
                    config.CpuCores = Math.Clamp(
                        (int)Math.Min(
                            item.Elements().FirstOrDefault(e => e.Name.LocalName == "VirtualQuantity")?.ParseNumber() ?? 2,
                            int.MaxValue),
                        1, Math.Max(1, Environment.ProcessorCount));
                    break;
                case ResMemory:
                {
                    var qty = item.Elements().FirstOrDefault(e => e.Name.LocalName == "VirtualQuantity")?.ParseNumber() ?? 2048;
                    var mib = ToMiB(qty,
                        item.Elements().FirstOrDefault(e => e.Name.LocalName == "AllocationUnits")?.Value ?? "MegaBytes");
                    // 与创建向导/设置修改同一套宿主钳制：OVF 的数量没有可信上限，
                    // 恶意或单位错误的包不该产出启动即崩的配置
                    config.MemoryMiB = Library.HostResources.ClampMemoryMiB(Math.Min(mib, int.MaxValue));
                    break;
                }
                case ResEthernet:
                {
                    // 我方导出的模式扩展（gvm:NetworkMode）：不带它按 NAT（OVF 生态
                    // 默认）。带上则如实还原——"断开"不能往返一次变成 NAT
                    var modeEl = item.Elements().FirstOrDefault(e => e.Name.LocalName == "NetworkMode"
                        && e.Name.Namespace == OvfExporter.GvmNs);
                    var mode = NetworkMode.Nat;
                    if (modeEl is not null && Enum.TryParse<NetworkMode>(modeEl.Value.Trim(), ignoreCase: true, out var m))
                        mode = m;
                    var virtualNetworkId = item.Elements().FirstOrDefault(e => e.Name.LocalName == "VirtualNetworkId"
                        && e.Name.Namespace == OvfExporter.GvmNs)?.Value.Trim();
                    var bridgeAdapter = item.Elements().FirstOrDefault(e => e.Name.LocalName == "BridgeAdapter"
                        && e.Name.Namespace == OvfExporter.GvmNs)?.Value.Trim();
                    // Host-only 是宿主级资源，第三方 OVF 不可能携带本机 network id。
                    // 不留下一个启动必崩的 HostOnly(null)：安全降级为断开并给出可见
                    // 警告，待用户在网络设置中重新选择宿主虚拟网络。
                    if (mode == NetworkMode.HostOnly && string.IsNullOrWhiteSpace(virtualNetworkId))
                    {
                        warnings.Add($"网卡 #{config.Devices.OfType<NetworkDevice>().Count() + 1} 的 Host-only 网络无法随 OVF 携带，已暂时断开；请在网络设置中重新选择宿主虚拟网络。");
                        mode = NetworkMode.Disconnected;
                    }
                    config.Devices.Add(new NetworkDevice
                    {
                        Mode = mode,
                        VirtualNetworkId = virtualNetworkId,
                        BridgeAdapter = bridgeAdapter,
                        CreatedOrder = ++order,
                    });
                    break;
                }
                case ResCdDvd:
                {
                    var hostRef = item.Elements().FirstOrDefault(e => e.Name.LocalName == "HostResource")?.Value;
                    string? iso = null;
                    if (hostRef is not null && TryResolveFileRef(hostRef, files, baseDir, out var href)) iso = href;
                    config.Devices.Add(new CdromDevice { IsoPath = iso, CreatedOrder = ++order });
                    break;
                }
                case ResHardDisk or ResDiskDrive:
                {
                    var hostRef = item.Elements().FirstOrDefault(e => e.Name.LocalName == "HostResource")?.Value
                                  ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "HostedFile")?.Value;
                    if (hostRef is not null && TryResolveDisk(hostRef, disks, files, baseDir, out var file, out var capacity, out var srcFormat))
                    {
                        // TryResolveDisk 返回 true ⇒ file 非 null（签名保守地声明为可空）
                        // 目标名去重：OVF 允许不同子目录引用同名磁盘文件（多盘信封常见），
                        // 只按文件名落位会互相覆盖——第二个转换撞 overwrite:false 直接裸抛
                        // IOException，几小时的转换白做。撞名时按序号前缀区分；
                        // 序号形态本身也可能撞（第一张盘就叫 disk2-data）——循环加码
                        // 直到占位成功
                        var baseName = Path.GetFileNameWithoutExtension(file);
                        var rel = $"disks/{baseName}.qcow2";
                        if (!usedDiskTargets.Add(rel))
                        {
                            var seq = diskImports.Count + 1;
                            do
                            {
                                rel = $"disks/disk{seq}-{baseName}.qcow2";
                                seq++;
                            } while (!usedDiskTargets.Add(rel));
                        }
                        diskImports.Add(new DiskImport(file!, capacity, rel, srcFormat));
                        // 能映射的原生设备：硬盘（IDE/SCSI 控制器 → Profile 总线）
                        config.Devices.Add(new DiskDevice { Path = rel, SizeBytes = capacity, CreatedOrder = ++order });
                    }
                    else
                    {
                        warnings.Add($"无法解析磁盘引用：{hostRef ?? "(空)"}");
                    }
                    break;
                }
                case ResUsbController:
                    // 启停状态跟 gvm:Usb/@enabled 走（我方导出会同时写出两者；
                    // 第三方信封只有 Item → 默认启用）
                    config.Devices.Add(new UsbControllerDevice
                    {
                        Enabled = GvmUsbEnabled ?? true,
                        CreatedOrder = ++order,
                    });
                    break;
                case ResVideo:
                    config.Devices.Add(new DisplayDevice { CreatedOrder = ++order });
                    break;
                default:
                {
                    // 无法安全映射 → Raw/兼容设备或"完全无法支持"。
                    // 与 QemuArgs 同一内容白名单：这一分支生成的参数同样要过审
                    // （-device 对 + 无宿主文件引用），白名单外不允许带着参数入列
                    var args = OvfItemToQemuArgs(item, scsiIdx++);
                    if (args.Count > 0 && AreSafeCompatArgs(args))
                    {
                        config.Devices.Add(new RawDevice
                        {
                            Label = caption.Length > 0 ? caption : $"OVF 资源 {type}",
                            Arguments = args,
                            CreatedOrder = ++order,
                        });
                        warnings.Add($"设备“{caption}”无法映射为原生设备，已保留为兼容设备（只读，只能删除后重新添加）。");
                    }
                    else
                    {
                        unsupported.Add($"{caption}（ResourceType={type}）{(description.Length > 0 ? "：" + description : "")}");
                    }
                    break;
                }
            }
        }

        if (!config.HasDisplayDevice) config.Devices.Add(new DisplayDevice { CreatedOrder = ++order });
        if (config.Devices.OfType<NetworkDevice>().Count() == 0)
            config.Devices.Add(new NetworkDevice { Mode = NetworkMode.Nat, CreatedOrder = ++order });

        // 我方导出的系统 Profile 扩展（gvm:OsProfile）：它决定固件（UEFI/BIOS +
        // SecureBoot）、TPM 与芯片组/总线形态。没有它只能按 "other"（BIOS/pc/IDE）
        // 处理——UEFI 装的客户机大概率起不来。已知的 Profile 还原固件设置并补齐
        // 随 Profile 走的设备（声卡/USB/TPM——OVF 没有对应资源类型）；未知 id
        // （更新版本导出的）按默认处理并明确警告
        var profileEl = vs?.Elements().FirstOrDefault(e => e.Name.LocalName == "OsProfile"
            && e.Name.Namespace == OvfExporter.GvmNs);
        GrassCore.Profiles.OsProfile? resolvedProfile = null;
        if (profileEl is not null)
        {
            var profileId = (string?)profileEl.Attribute("id");
            resolvedProfile = GrassCore.Profiles.OsProfileLibrary.List()
                .FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
            var profile = resolvedProfile;
            if (profile is not null)
            {
                config.OsProfileId = profile.Id;
                config.Firmware = new Config.FirmwareSettings
                {
                    Kind = profile.Firmware,
                    SecureBoot = profile.SecureBoot,
                    Tpm = profile.Tpm,
                };
                if (profile.Tpm && config.Devices.OfType<Config.TpmDevice>().Any() == false)
                    config.Devices.Add(new Config.TpmDevice { Enabled = true, CreatedOrder = ++order });
            }
            else
            {
                warnings.Add($"导出方的系统配置档案（{profileId}）此版本不认识：固件按默认（BIOS）处理，" +
                             "UEFI 安装的系统可能无法启动。请用新版应用导入。");
            }
        }

        // 固件口径统一（profile 没解析出来的所有路径：无 gvm:OsProfile 的第三方
        // 信封、以及【未知 id】——新版本导出的档案此版本不认识）：预检按
        // 【config.Firmware】决定要不要 NVRAM，构建器按【Profile】选机型/固件——
        // 两个事实源不一致时，"other"（BIOS/pc）会被 VmConfiguration 的默认值
        // （Uefi）顶成假 UEFI：预检要 NVRAM、构建器根本不开 UEFI，永远起不来
        if (resolvedProfile is null)
        {
            var eff = GrassCore.Profiles.OsProfileLibrary.List()
                .FirstOrDefault(p => string.Equals(p.Id, config.OsProfileId, StringComparison.OrdinalIgnoreCase));
            if (eff is not null)
            {
                config.Firmware = new Config.FirmwareSettings
                {
                    Kind = eff.Firmware,
                    SecureBoot = eff.SecureBoot,
                    Tpm = eff.Tpm,
                };
            }
        }

        // 声卡/USB 的 Profile 默认补齐（对【所有】信封生效，含第三方）：gvm 标志
        // 明确 false（用户关掉）才跳过；缺失/未表态 = 按 Profile 默认补——第三方
        // 信封没有这两个扩展，不补的话导入的 VM 永远无声、设置页也无从开启
        if (GvmAudioEnabled != false && config.Devices.OfType<Config.AudioDevice>().Any() == false)
            config.Devices.Add(new Config.AudioDevice { Enabled = true, CreatedOrder = ++order });
        if (GvmUsbEnabled != false && config.Devices.OfType<Config.UsbControllerDevice>().Any() == false)
            config.Devices.Add(new Config.UsbControllerDevice { Enabled = true, CreatedOrder = ++order });

        return new ImportPlan(config, diskImports, warnings, unsupported);
    }

    private static XNamespace OvfNames(XDocument doc)
    {
        // 默认命名空间即 envelope 命名空间（Item/Rasd 等元素多数实现在默认 ns 或 rasd ns 下）
        return doc.Root?.Name.Namespace ?? XNamespace.None;
    }

    /// <summary>空间预估（§15.2）：转换磁盘所需 ≈ Σ(max(物理大小, 虚拟容量))（QCOW2 稀疏写入，按物理量估算下限）。</summary>
    public static long EstimateRequiredBytes(IEnumerable<DiskImport> disks)
    {
        // 饱和加法：畸形信封的多盘大数值相加一旦回绕成负数，"avail < required"
        // 恒为假——计划警告与执行硬校验被双双静默绕过。饱和到 long.MaxValue
        // 只会让预检如实报"空间不够"，绝不会假装够。
        // 每盘按 max(虚拟, 物理)（=文档契约）：源文件通常不在存档卷（.ovf 目录/
        // OVA 临时目录都在别处），把物理量再加一遍会把预估翻倍——执行前的
        // 硬校验就会拒绝明明放得下的导入
        long total = 0;
        foreach (var d in disks)
        {
            var physical = File.Exists(d.SourceFile) ? new FileInfo(d.SourceFile).Length : 0;
            var need = Math.Max(Math.Max(physical, 0), d.VirtualSizeBytes);
            if (need < 0) need = long.MaxValue; // 单项溢出同样饱和
            total = total + need < 0 ? long.MaxValue : total + need;
        }
        return total;
    }

    /// <summary>执行导入：解包目标包结构 + VMDK/RAW → QCOW2 事务转换 + config 落盘。</summary>
    public async Task<GrassVmPackage> ExecuteAsync(ImportPlan plan, string libraryRoot, string vmName,
        bool allowUnsupported, CancellationToken ct = default)
    {
        if (plan.BlocksImport && !allowUnsupported)
            throw new GrassCoreException(
                "以下设备完全无法支持：" + string.Join("；", plan.UnsupportedDevices) +
                "。除非选择“仍然导入”，否则导入已被阻止。");
        var pkg = GrassVmPackage.CreateNew(libraryRoot, vmName);
        try
        {
            foreach (var d in plan.Disks)
            {
                var targetAbs = Path.Combine(pkg.Path, d.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar));
                // 源镜像安全预检（宽松查询把"失败"与"无 backing"都折叠成 null，
                // 在这里不够保守——用严格版）：
                // ① 带宿主 backing 引用的镜像：把声明的 VMDK 换成带 backing 的
                //    qcow2，转换会把解析到的宿主文件内容一并烘进导入盘——
                //    宿主任意文件就这样流进客户机。拒绝，无例外
                // ② info 都读不出的镜像：转换（同样要探测）只会以更难懂的方式
                //    失败——提前给出明确错误
                string? backing;
                try { backing = diskOps.QueryBackingFileStrict(d.SourceFile); }
                catch (GrassCore.Qemu.QemuImgException e)
                {
                    throw new GrassCoreException($"磁盘镜像无法识别（{Path.GetFileName(d.SourceFile)}：{e.Message}），已拒绝导入。");
                }
                if (backing is not null)
                    throw new GrassCoreException(
                        $"磁盘镜像 {Path.GetFileName(d.SourceFile)} 带 backing 引用（{backing}）——" +
                        "导入它可能把宿主文件内容带进虚拟机，已拒绝。");
                // VMDK 等统一转 QCOW2（事务式：.grass-tmp + 校验 + 原子替换）。
                // 信封声明的格式能映射成 qemu-img 格式名时显式 -f 锁定，
                // 不给"声明 A 实为 B"的镜像靠探测蒙混的余地
                await diskOps.ConvertToQcow2Async(d.SourceFile, targetAbs, MapSourceFormat(d.SourceFormat), ct);
            }
            // CD 引用的 ISO：OVA 解包在临时目录、用完即删——原样引用会悬空。
            // 拷进包内（isovol/）并改成包内相对引用；拷不动的清空引用（导入照常，换盘即可）。
            // 目标名带光驱序号：两个光驱引用【不同目录下同名】ISO 是合法形态，
            // 只按文件名落位会互相覆盖，前一个光驱静默拿到错误的介质
            Directory.CreateDirectory(Path.Combine(pkg.Path, "isovol"));
            var cdOrdinal = 0;
            foreach (var cd in plan.Config.Devices.OfType<CdromDevice>().ToList())
            {
                cdOrdinal++;
                if (cd.IsoPath is null) continue;
                var srcIso = Path.IsPathRooted(cd.IsoPath)
                    ? cd.IsoPath
                    : Path.GetFullPath(Path.Combine(libraryRoot, cd.IsoPath));
                if (!File.Exists(srcIso))
                {
                    cd.IsoPath = null;
                    continue;
                }
                var dst = Path.Combine(pkg.Path, "isovol",
                    $"cd{cdOrdinal}-{SanitizeFileName(Path.GetFileNameWithoutExtension(srcIso))}{Path.GetExtension(srcIso)}");
                File.Copy(srcIso, dst, overwrite: true);
                cd.IsoPath = PathPolicy.NormalizeReference(pkg, dst);
            }
            // 选择"仍然导入"：不可用设备以 Raw(Unsupported) 保留，VM 可能无法运行（用户已被告知）。
            // QemuArgs 设备在计划阶段就已带参入列（Unsupported=true）——只给【还没有
            // 对应设备】的项补占位，否则同一设备在设置页出现两条
            if (allowUnsupported)
            {
                var order = plan.Config.Devices.Select(d => d.CreatedOrder).DefaultIfEmpty(0).Max();
                var existingLabels = plan.Config.Devices
                    .OfType<RawDevice>()
                    .Select(d => d.Label)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var label in plan.UnsupportedDevices)
                {
                    if (existingLabels.Contains(label)) continue;
                    plan.Config.Devices.Add(new RawDevice { Label = label, Unsupported = true, CreatedOrder = ++order });
                }
            }
            plan.Config.Name = vmName;
            // UEFI 的 NVRAM 变量区：像 CreateVm/克隆一样从模板落一份。不落的
            // 话启动预检直接判"固件数据缺失"拒绝启动，而设置页没有重建入口
            // = 导入出一台永久开不了机的 VM（我们自己的往返导出也走这条路径）
            if (plan.Config.Firmware?.Kind == GrassCore.Config.FirmwareKind.Uefi)
            {
                if (ovmfVarsTemplate is not null && File.Exists(ovmfVarsTemplate))
                {
                    Directory.CreateDirectory(pkg.FirmwarePath);
                    File.Copy(ovmfVarsTemplate, Path.Combine(pkg.FirmwarePath, "VARS.fd"), overwrite: true);
                }
                else
                {
                    plan.Warnings.Add("固件模板缺失：UEFI 虚拟机的 NVRAM 未初始化，首次启动可能被预检拒绝。");
                }
            }
            new ConfigStore(pkg).Save(plan.Config);
            return pkg;
        }
        catch
        {
            // 回滚清场绝不改写原始错误：杀毒/索引器短暂占用刚转换的磁盘是
            // 常态，Delete 抛了 IOException 会顶掉真正的失败原因；留下的
            // 无 config 骨架会让同名重试永远撞"已存在"，ScanLibrary 又只给
            // 死路列表——与 CreateVm 的回滚同一套防御
            try { if (Directory.Exists(pkg.Path)) Directory.Delete(pkg.Path, recursive: true); }
            catch { /* 残留骨架：下次换名重试或手工清理 */ }
            throw;
        }
    }

    /// <summary>解 OVA（tar）到目录，返回其中的 .ovf 文档路径。</summary>
    public static string ExtractOva(string ovaPath, string destDir)
    {
        if (GrassVmPackage.ContainsReparsePoint(destDir))
            throw new GrassCoreException("OVA 解压目录不能通过符号链接或目录联接访问。");
        Directory.CreateDirectory(destDir);
        const int maxEntries = 100_000;
        const long maxEntryBytes = 128L * 1024 * 1024 * 1024;
        const long maxTotalBytes = 512L * 1024 * 1024 * 1024;
        long total = 0;
        using var input = File.OpenRead(ovaPath);
        using var reader = new TarReader(input);
        TarEntry? entry;
        var count = 0;
        var root = Path.GetFullPath(destDir);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (++count > maxEntries) throw new GrassCoreException("OVA 条目数量超过安全上限。");
            var name = entry.Name.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destDir, name));
            // OVA 是不可信的 tar：路径边界必须按宿主文件系统的大小写语义检查。
            // Linux/macOS 上若固定使用 OrdinalIgnoreCase，../BASE/evil 会被当成
            // 位于 base 目录内；随后 ExtractToFile 可把文件写到其兄弟目录。
            if (!target.StartsWith(rootPrefix, pathComparison))
                throw new GrassCoreException("OVA 包含非法路径，已拒绝导入。");
            if (entry.EntryType is TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            // Tar 可以携带符号链接、硬链接和设备节点。不能把这些条目交给
            // ExtractToFile：它们可能在解包阶段重新指向 destDir 外部，或把宿主
            // 设备暴露成普通文件。OVA 导入只接受普通文件与目录。
            if (entry.EntryType is not TarEntryType.RegularFile)
                throw new GrassCoreException("OVA 包含不支持的链接或特殊文件条目，已拒绝导入。");
            if (entry.Length > maxEntryBytes || (total = checked(total + entry.Length)) > maxTotalBytes)
                throw new GrassCoreException("OVA 解压内容超过安全配额，已拒绝导入。");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        var ovf = Directory.EnumerateFiles(destDir, "*.ovf", SearchOption.AllDirectories).FirstOrDefault();
        // tar 条目路径安全：ExtractToDirectory 已做路径规范化；额外校验不逃逸
        return ovf ?? throw new GrassCoreException("OVA 中找不到 .ovf 描述文件。");
    }

    /// <summary>
    /// 兼容设备原始参数白名单：只放行成对的 -device &lt;型号&gt;，且型号串里不得有
    /// 宿主文件引用（file= 等在 QemuCommandBuilder 还有第二道拦截）。
    /// 其余一切参数（-drive、-readconfig、-set……）都不认——出处标记可伪造，
    /// 只有内容可信才算可信。
    /// </summary>
    private static bool AreSafeCompatArgs(List<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (i % 2 == 0)
            {
                if (!string.Equals(args[i], "-device", StringComparison.Ordinal)) return false;
            }
            else
            {
                var v = args[i];
                if (v.StartsWith('-')) return false; // 选项串当了值：结构不对
                // 与 QemuCommandBuilder.DeviceValueReferencesHostFile 同一属性集
                // （romfile= 也是宿主文件引用——option ROM 能读宿主任意文件）
                if (GrassCore.Qemu.QemuCommandBuilder.DeviceValueReferencesHostFile(v)) return false;
            }
        }
        return args.Count % 2 == 0; // 必须是完整的 -device 值对
    }

    /// <summary>
    /// 信封声明的磁盘格式（DiskSection/@format，通常是 DMTF URI）→ qemu-img
    /// 格式名。映射不出就返回 null（退回探测——上面的 backing 预检仍然把关）。
    /// </summary>
    private static string? MapSourceFormat(string? format)
    {
        if (string.IsNullOrEmpty(format)) return null;
        var f = format.ToLowerInvariant();
        if (f.Contains("vmdk")) return "vmdk";
        if (f.Contains("qcow2")) return "qcow2";
        if (f.Contains("qcow")) return "qcow";
        if (f.Contains("vhdx")) return "vhdx";
        if (f.Contains("vhd") || f.Contains("vpc")) return "vpc";
        if (f.Contains("vdi")) return "vdi";
        if (f.Contains("raw")) return "raw";
        return null;
    }

    private static bool TryResolveDisk(string hostRef,
        Dictionary<string, (string FileRef, long Capacity, string? Format)> disks,
        Dictionary<string, string> files, string baseDir,
        out string? file, out long capacity, out string? format)
    {
        file = null; capacity = 0; format = null;
        var id = hostRef.Split('/').Last();
        if (!disks.TryGetValue(id, out var d)) return false;
        if (!files.TryGetValue(d.FileRef, out var href)) return false;
        file = ResolveContained(baseDir, href);
        capacity = d.Capacity;
        format = d.Format;
        return file is not null && File.Exists(file);
    }

    private static bool TryResolveFileRef(string hostRef, Dictionary<string, string> files, string baseDir, out string href)
    {
        href = "";
        var id = hostRef.Split('/').Last();
        if (!files.TryGetValue(id, out var f)) return false;
        var resolved = ResolveContained(baseDir, f);
        if (resolved is null) return false;
        href = resolved;
        return File.Exists(href);
    }

    /// <summary>
    /// 档案内引用只允许落在 baseDir 之内（OVA = 解包目录，OVF = 所在目录）。
    /// 绝对路径与 ../ 穿越一律拒绝：否则恶意档案能把宿主任意文件（convert 成
    /// 磁盘或 Copy 进包里）静默搬进导入的 VM——用户在计划页还只看得到文件名。
    /// </summary>
    private static string? ResolveContained(string baseDir, string href)
    {
        try
        {
            // OVF href 是 URI 引用：标准档案会把空格等写成 %20。先 URI 解码，
            // 再做 rooted/contained 检查（解码后的 %2e%2e 同样会被穿越检查挡下）。
            var decoded = Uri.UnescapeDataString(href);
            // 绝对路径：直接不算档案内容（哪怕真实存在）
            if (Path.IsPathRooted(decoded)) return null;
            var root = Path.GetFullPath(baseDir);
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return null;
            var resolved = Path.GetFullPath(Path.Combine(root, decoded));
            // 前缀比较的大小写语义跟着文件系统走：Windows/原生不区分大小写 →
            // OrdinalIgnoreCase；Linux（区分大小写）必须 Ordinal——../BASE 这种
            // 只差大小写的兄弟目录穿越在 OrdinalIgnoreCase 下会被放行
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return resolved.StartsWith(root + Path.DirectorySeparatorChar, cmp)
                && (!File.Exists(resolved) || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) == 0)
                && !HasReparsePointBetween(root, resolved)
                ? resolved
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasReparsePointBetween(string root, string path)
    {
        var current = new DirectoryInfo(path);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        while (current is not null && !string.Equals(current.FullName, root, cmp))
        {
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { return true; }
            catch (IOException) { return true; }
            current = current.Parent;
        }
        return false;
    }

    private static long ParseCapacity(string cap, string? allocationUnits)
    {
        // 磁盘容量域上限 64TiB：所有量纲换算走 decimal、越界归零——位移/乘法
        // 的静默回绕（负数、10^20 这类怪值）会流进计划 UI 与空间预检
        const decimal domainMax = 64m * 1024 * 1024 * 1024 * 1024;
        var value = 0L;
        var parts = cap.Split('*');
        if (parts.Length == 2 && long.TryParse(parts[0].Trim(), out var v) &&
            parts[1].Trim().StartsWith("2^", StringComparison.Ordinal) &&
            int.TryParse(parts[1].Trim()[2..], out var exp))
        {
            // C# 的移位数会按 63 掩码回绕（2^66 静默变 2^2）：越界指数一律按
            // 解析失败处理，容量归零 → 导入预检给出明确错误而非错值
            if (exp is >= 0 and <= 50 && v > 0)
            {
                var factor = (decimal)Math.Pow(2, exp);
                if ((decimal)v <= domainMax / factor) value = (long)((decimal)v * factor);
            }
        }
        else if (!long.TryParse(cap.Trim(), out value))
        {
            value = 0;
        }
        if (value < 0) value = 0; // 负数量（畸形信封）不流入计划
        // 无单位/纯 byte 单位的【明文数值】路径同样受域上限约束：信封里
        // long.MaxValue 这种怪值若原样放行，EstimateRequiredBytes 的长整型
        // 加法会回绕成负数——空间预检的警告与硬校验被双双静默绕过
        if ((decimal)value > domainMax) value = 0;
        if (string.IsNullOrEmpty(allocationUnits)) return value; // 默认字节
        // 形如 "byte * 2^30" / "bytes" / "KB" / "MegaBytes" / "Gigabytes"
        var u = allocationUnits.Trim().ToLowerInvariant().Replace("byte", "").Replace("*", "").Trim();
        if (u.StartsWith("2^", StringComparison.Ordinal) && int.TryParse(u[2..], out var unitExp))
        {
            if (unitExp is < 0 or > 40) return 0; // 同上：越界不回绕
            var factorU = (decimal)Math.Pow(2, unitExp);
            if (value <= 0 || (decimal)value > domainMax / factorU) return 0;
            return (long)((decimal)value * factorU);
        }
        if (u.Length == 0) return value;
        var mul = u[0] switch
        {
            'k' => 1024m,
            'm' => 1024m * 1024,
            'g' => 1024m * 1024 * 1024,
            't' => 1024m * 1024 * 1024 * 1024,
            _ => 1m,
        };
        if (value <= 0 || (decimal)value > domainMax / mul) return 0;
        return (long)((decimal)value * mul);
    }

    private static long ToMiB(long qty, string units)
    {
        // CIM/OVF 常见两种写法："byte*" 与 "byte * 2^30"（带空格的指数形式）。
        // 指数形式【必须先于】前缀归一化处理：旧代码把它换成 "g" 后，
        // switch 里只有 "gb" 没有 "g" → 落进"未知单位按 MB"，8GiB 静默变 8MiB，
        // 再被宿主钳制抬回 512MB 下限——用户拿到一台没法用的机器且毫无提示
        var u = units.Trim().ToLowerInvariant().Replace(" ", "");
        if (u.StartsWith("byte*2^", StringComparison.Ordinal)
            && int.TryParse(u["byte*2^".Length..], out var exp))
        {
            // 指数越界（恶意/损坏的信封，2^9999）会让 Math.Pow 溢出成 ∞，
            // (decimal)∞ 直接抛裸 OverflowException。越界按"未知单位"保守
            // 处理（数量按 MB），交给宿主钳制兜底——承诺的是钳制不是崩溃。
            // 巨大的明文数量 × 合法指数同样会把 decimal 乘法顶爆（1e19×2^40
            // ≈ 1e31 > decimal.MaxValue，乘法【总是】抛）——先比后乘
            if (exp is >= 0 and <= 40)
            {
                var factor = (decimal)Math.Pow(2, exp);
                if ((decimal)qty > (decimal)long.MaxValue / factor * 1024 * 1024)
                    return qty; // 乘出来必然超任何宿主内存：按未知单位保守处理
                // qty 个"2^exp 字节"= qty × 2^exp 字节 → MiB（decimal 防大指数溢出）
                return (long)((decimal)qty * factor / (1024 * 1024));
            }
            return qty;
        }
        u = u.Replace("byte*2^0", "b").Replace("byte*", "b");
        // 前缀路径统一 decimal 先比后乘：明文数量不受 ParseNumber 上限约束，
        // "g"/"t" 的 long 乘法会静默回绕成负数 → 钳制地板 512MiB（而不是
        // 承诺的宿主上限）。未知单位按 MB 保守处理
        var mul = u switch
        {
            "b" or "bytes" or "byte" => 1m / (1024 * 1024),
            // DMTF 单位名单复数都合法（CIM 规范两种拼写都出现）
            "k" or "kb" or "kilobytes" or "kilobyte" => 1m / 1024,
            "m" or "mb" or "megabytes" or "megabyte" => 1m,
            "g" or "gb" or "gigabytes" or "gigabyte" => 1024m,
            "t" or "tb" or "terabytes" or "terabyte" => 1024m * 1024,
            _ => decimal.MinusOne,
        };
        if (mul == decimal.MinusOne) return qty;
        // 内存域上限 16TiB（以 MiB 计 = 16 × 2^20）：任何宿主之上，实际值交给 ClampMemoryMiB
        const decimal memCapMiB = 16m * 1024 * 1024;
        var dm = (decimal)qty * mul;
        return dm > memCapMiB ? (long)memCapMiB : (long)dm;
    }

    /// <summary>OVF Item → 可传递给 QEMU 的原始参数（保守映射；空 = 完全无法支持）。</summary>
    private static List<string> OvfItemToQemuArgs(XElement item, int scsiIndex)
    {
        // 只把极少数确定安全的设备转成参数；其余一律视为"完全无法支持"交由用户决策
        var type = (int?)item.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceType")?.ParseNumber() ?? -1;
        var caption = item.Elements().FirstOrDefault(e => e.Name.LocalName == "Caption")?.Value ?? "";
        if (type is 5 or 6 && caption.Contains("SCSI", StringComparison.OrdinalIgnoreCase))
        {
            // 多个 SCSI 控制器各自拿唯一的 id：重复的 id= 会让 QEMU 启动即失败
            return ["-device", $"lsi53c895a,id=scsi{scsiIndex}"];
        }
        return [];
    }

    /// <summary>包内文件名安全化（ISO 拷入 isovol/ 时）。</summary>
    private static string SanitizeFileName(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var s = new string(chars).Trim('-');
        return s.Length == 0 ? "iso" : s;
    }
}

internal static class XElementNumber
{
    public static long? ParseNumber(this XElement e)
    {
        var v = e.Value.Trim();
        // "2^30" 形式。移位按 63 掩码回绕（2^66 静默变 2^2）+ 大基数照样回绕：
        // 全程先比后乘（decimal 乘法本身会抛 OverflowException），按域上限
        // （≤64TiB）钳制，越界返回 null 让调用方走默认值，绝不让包装值流进
        // 计划与空间估算
        const decimal parseMax = 64m * 1024 * 1024 * 1024 * 1024;
        if (v.Contains('^') && v.Split('^') is [var b, var ex] &&
            long.TryParse(b.Trim(), out var bv) && int.TryParse(ex.Trim(), out var ev)
            && ev is >= 0 and <= 50 && bv > 0)
        {
            var factor = (decimal)Math.Pow(2, ev);
            if ((decimal)bv > parseMax / factor) return null;
            var d = (decimal)bv * factor;
            return d is > 0 and <= parseMax ? (long)d : null;
        }
        return long.TryParse(v, out var n) ? n : null;
    }
}
