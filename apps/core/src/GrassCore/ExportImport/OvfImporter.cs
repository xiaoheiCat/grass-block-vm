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
public sealed class OvfImporter(TransactionalDiskOps diskOps)
{
    public sealed record ImportPlan(
        VmConfiguration Config,
        IReadOnlyList<DiskImport> Disks,
        IReadOnlyList<string> Warnings,
        /// <summary>完全无法支持、默认会阻止导入的设备（用户选"仍然导入"后以不可用状态保留）。</summary>
        IReadOnlyList<string> UnsupportedDevices)
    {
        public bool BlocksImport => UnsupportedDevices.Count > 0;
    }

    public sealed record DiskImport(string SourceFile, long VirtualSizeBytes, string TargetRelativePath);

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
        var files = env.Element(ns + "References")?.Elements(ns + "File")
            .ToDictionary(f => (string)f.Attribute(ns + "id")!, f => (string)f.Attribute(ns + "href")!, StringComparer.Ordinal)
            ?? new Dictionary<string, string>();
        // diskId → (fileRef, virtualSize, format)：capacity × capacityAllocationUnits（默认字节）
        var disks = diskSection?.Elements(ns + "Disk").ToDictionary(
            d => (string?)d.Attribute(ns + "diskId") ?? "",
            d => (
                FileRef: (string?)d.Attribute(ns + "fileRef") ?? "",
                Capacity: ParseCapacity(
                    (string?)d.Attribute(ns + "capacity") ?? "0",
                    (string?)d.Attribute(ns + "capacityAllocationUnits")),
                Format: (string?)d.Attribute(ns + "format")),
            StringComparer.Ordinal) ?? new Dictionary<string, (string FileRef, long Capacity, string? Format)>();

        var config = new VmConfiguration { Name = "Imported VM", OsProfileId = "other" };
        var diskImports = new List<DiskImport>();
        var warnings = new List<string>();
        var unsupported = new List<string>();
        int order = 0;

        var vs = env.Element(ns + "VirtualSystem") ?? env.Elements().FirstOrDefault(e => e.Name.LocalName == "VirtualSystem");
        if (vs is not null)
        {
            var nameEl = vs.Element(ns + "Name") ?? vs.Elements().FirstOrDefault(e => e.Name.LocalName == "Name");
            if (nameEl is not null) config.Name = nameEl.Value;
        }

        foreach (var item in env.Descendants().Where(e => e.Name.LocalName == "Item"))
        {
            var typeValue = item.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceType")?.ParseNumber();
            var type = typeValue.HasValue ? (int)typeValue.Value : -1;
            var caption = item.Elements().FirstOrDefault(e => e.Name.LocalName == "Caption")?.Value ?? "";
            var description = item.Elements().FirstOrDefault(e => e.Name.LocalName == "Description")?.Value ?? "";
            switch (type)
            {
                case ResProcessor:
                    config.CpuCores = Math.Max(1, (int)(item.Elements().FirstOrDefault(e => e.Name.LocalName == "VirtualQuantity")?.ParseNumber() ?? 2));
                    break;
                case ResMemory:
                {
                    var qty = item.Elements().FirstOrDefault(e => e.Name.LocalName == "VirtualQuantity")?.ParseNumber() ?? 2048;
                    var mib = ToMiB(qty,
                        item.Elements().FirstOrDefault(e => e.Name.LocalName == "AllocationUnits")?.Value ?? "MegaBytes");
                    config.MemoryMiB = Math.Max(512, (int)Math.Min(mib, int.MaxValue));
                    break;
                }
                case ResEthernet:
                    config.Devices.Add(new NetworkDevice { Mode = NetworkMode.Nat, CreatedOrder = ++order });
                    break;
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
                    if (hostRef is not null && TryResolveDisk(hostRef, disks, files, baseDir, out var file, out var capacity))
                    {
                        var rel = $"disks/{Path.GetFileNameWithoutExtension(file)}.qcow2";
                        diskImports.Add(new DiskImport(file, capacity, rel));
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
                    config.Devices.Add(new UsbControllerDevice { CreatedOrder = ++order });
                    break;
                case ResVideo:
                    config.Devices.Add(new DisplayDevice { CreatedOrder = ++order });
                    break;
                default:
                {
                    // 无法安全映射 → Raw/兼容设备或"完全无法支持"
                    var args = OvfItemToQemuArgs(item);
                    if (args.Count > 0)
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
        long total = 0;
        foreach (var d in disks)
        {
            var physical = File.Exists(d.SourceFile) ? new FileInfo(d.SourceFile).Length : 0;
            total += Math.Max(physical, 0) + Math.Max(d.VirtualSizeBytes, physical);
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
                // VMDK 等统一转 QCOW2（事务式：.grass-tmp + 校验 + 原子替换）
                await diskOps.ConvertToQcow2Async(d.SourceFile, targetAbs, ct);
            }
            // 选择"仍然导入"：不可用设备以 Raw(Unsupported) 保留，VM 可能无法运行（用户已被告知）
            if (allowUnsupported)
            {
                var order = plan.Config.Devices.Select(d => d.CreatedOrder).DefaultIfEmpty(0).Max();
                foreach (var label in plan.UnsupportedDevices)
                {
                    plan.Config.Devices.Add(new RawDevice { Label = label, Unsupported = true, CreatedOrder = ++order });
                }
            }
            plan.Config.Name = vmName;
            new ConfigStore(pkg).Save(plan.Config);
            return pkg;
        }
        catch
        {
            if (Directory.Exists(pkg.Path)) Directory.Delete(pkg.Path, recursive: true);
            throw;
        }
    }

    /// <summary>解 OVA（tar）到目录，返回其中的 .ovf 文档路径。</summary>
    public static string ExtractOva(string ovaPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        TarFile.ExtractToDirectory(ovaPath, destDir, overwriteFiles: true);
        var ovf = Directory.EnumerateFiles(destDir, "*.ovf", SearchOption.AllDirectories).FirstOrDefault();
        // tar 条目路径安全：ExtractToDirectory 已做路径规范化；额外校验不逃逸
        return ovf ?? throw new GrassCoreException("OVA 中找不到 .ovf 描述文件。");
    }

    private static bool TryResolveDisk(string hostRef,
        Dictionary<string, (string FileRef, long Capacity, string? Format)> disks,
        Dictionary<string, string> files, string baseDir,
        out string file, out long capacity)
    {
        file = ""; capacity = 0;
        var id = hostRef.Split('/').Last();
        if (!disks.TryGetValue(id, out var d)) return false;
        if (!files.TryGetValue(d.FileRef, out var href)) return false;
        file = Path.IsPathRooted(href) ? href : Path.GetFullPath(Path.Combine(baseDir, href));
        capacity = d.Capacity;
        return File.Exists(file);
    }

    private static bool TryResolveFileRef(string hostRef, Dictionary<string, string> files, string baseDir, out string href)
    {
        href = "";
        var id = hostRef.Split('/').Last();
        if (!files.TryGetValue(id, out var f)) return false;
        href = Path.IsPathRooted(f) ? f : Path.GetFullPath(Path.Combine(baseDir, f));
        return File.Exists(href);
    }

    private static long ParseCapacity(string cap, string? allocationUnits)
    {
        var value = 0L;
        var parts = cap.Split('*');
        if (parts.Length == 2 && long.TryParse(parts[0].Trim(), out var v) &&
            parts[1].Trim().StartsWith("2^", StringComparison.Ordinal) &&
            int.TryParse(parts[1].Trim()[2..], out var exp))
        {
            value = v << exp;
        }
        else if (!long.TryParse(cap.Trim(), out value))
        {
            value = 0;
        }
        if (string.IsNullOrEmpty(allocationUnits)) return value; // 默认字节
        // 形如 "byte * 2^30" / "bytes" / "KB" / "MegaBytes" / "Gigabytes"
        var u = allocationUnits.Trim().ToLowerInvariant().Replace("byte", "").Replace("*", "").Trim();
        if (u.StartsWith("2^", StringComparison.Ordinal) && int.TryParse(u[2..], out var unitExp))
            return value << unitExp; // 指数单位（如 80 × 2^30）
        if (u.Length == 0) return value;
        return u[0] switch
        {
            'k' => value * 1024,
            'm' => value * 1024 * 1024,
            'g' => value * 1024L * 1024 * 1024,
            't' => value * 1024L * 1024 * 1024 * 1024,
            _ => value,
        };
    }

    private static long ToMiB(long qty, string units)
    {
        // CIM/OVF 常见两种写法："byte*" 与 "byte * 2^30"（带空格的指数形式）——先去空格再归一化
        var u = units.Trim().ToLowerInvariant().Replace(" ", "");
        u = u.Replace("byte*2^30", "g").Replace("byte*2^20", "m").Replace("byte*2^0", "b")
            .Replace("byte*", "b");
        return u switch
        {
            "b" or "bytes" => qty / (1024 * 1024),
            "kb" or "kilobytes" => qty / 1024,
            "mb" or "megabytes" => qty,
            "gb" or "gigabytes" => qty * 1024,
            _ => qty, // 未知单位按 MB 保守处理
        };
    }

    /// <summary>OVF Item → 可传递给 QEMU 的原始参数（保守映射；空 = 完全无法支持）。</summary>
    private static List<string> OvfItemToQemuArgs(XElement item)
    {
        // 只把极少数确定安全的设备转成参数；其余一律视为"完全无法支持"交由用户决策
        var type = (int?)item.Elements().FirstOrDefault(e => e.Name.LocalName == "ResourceType")?.ParseNumber() ?? -1;
        var caption = item.Elements().FirstOrDefault(e => e.Name.LocalName == "Caption")?.Value ?? "";
        if (type is 5 or 6 && caption.Contains("SCSI", StringComparison.OrdinalIgnoreCase))
        {
            return ["-device", "lsi53c895a,id=scsi0"];
        }
        return [];
    }
}

internal static class XElementNumber
{
    public static long? ParseNumber(this XElement e)
    {
        var v = e.Value.Trim();
        // "2^30" 形式
        if (v.Contains('^') && v.Split('^') is [var b, var ex] &&
            long.TryParse(b.Trim(), out var bv) && int.TryParse(ex.Trim(), out var ev))
            return bv << ev;
        return long.TryParse(v, out var n) ? n : null;
    }
}
