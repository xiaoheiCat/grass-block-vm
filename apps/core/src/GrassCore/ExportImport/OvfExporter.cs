using System.Formats.Tar;
using System.Text;
using System.Xml.Linq;
using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Qemu;
using GrassCore.Rpc;

namespace GrassCore.ExportImport;

/// <summary>
/// OVF/OVF 目录导出（§15.1）：兼容交换格式，只导出当前有效状态，
/// 不承诺保留快照树/全部元数据。磁盘统一转 VMDK（流式优化）。
/// Raw/不可映射设备降级：以 ovf:Config 原样文本记录（再导入时回到 Raw 兼容设备）。
/// </summary>
public sealed class OvfExporter(TransactionalDiskOps diskOps)
{
    public async Task<string> ExportAsync(GrassVmPackage package, string destDir, CancellationToken ct = default)
    {
        var config = new ConfigStore(package).Load();
        Directory.CreateDirectory(destDir);
        var vmId = Sanitize(config.Name);

        // 先全量预检再动手转换：缺盘要在跑几百 GB 的 convert 【之前】失败，
        // 不要转换完全部在场盘才报"缺一块"
        var missing = config.Devices.OfType<DiskDevice>()
            .Where(d => !File.Exists(PathPolicy.Resolve(package, d.Path)))
            .Select(d => d.Path).ToList(); // 静默少盘的档案导入后少一块数据盘——明确失败
        if (missing.Count > 0)
            throw new GrassCoreException(
                $"以下磁盘文件缺失，无法导出 OVF：{string.Join("、", missing)}。请先在设置中重新定位这些文件。");

        var diskFiles = new List<(string Href, long Size, long Capacity)>();
        var idx = 0;
        foreach (var disk in config.Devices.OfType<DiskDevice>())
        {
            idx++;
            var src = PathPolicy.Resolve(package, disk.Path);
            var href = $"{vmId}-disk{idx}.vmdk";
            var dst = Path.Combine(destDir, href);
            // 清掉上次失败留下的半成品/旧档：convert 内部是 overwrite:false 的原子
            // 改名，对着旧目录重试导出会直接 IOException
            if (File.Exists(dst)) File.Delete(dst);
            await diskOps.ConvertAsync(src, dst, "vmdk", ct);
            diskFiles.Add((href, new FileInfo(dst).Length, disk.SizeBytes > 0 ? disk.SizeBytes : new FileInfo(dst).Length));
        }

        // 光盘介质随档案走：不带上 ISO 的话往返一次 CD/DVD 全变空盘（设备都被丢掉）
        var isoFiles = new List<(int CdOrdinal, string Href, long Size)>();
        var cdOrdinal = 0;
        foreach (var cd in config.Devices.OfType<CdromDevice>())
        {
            cdOrdinal++;
            if (cd.IsoPath is null) continue;
            var src = PathPolicy.Resolve(package, cd.IsoPath);
            if (!File.Exists(src)) continue; // 介质已不在：导出为空光驱（与"弹出"同语义）
            var href = $"{vmId}-cd{cdOrdinal}.iso";
            var dst = Path.Combine(destDir, href);
            if (File.Exists(dst)) File.Delete(dst);
            File.Copy(src, dst);
            isoFiles.Add((cdOrdinal, href, new FileInfo(dst).Length));
        }

        var ovf = BuildOvfXml(config, vmId, diskFiles, isoFiles);
        var ovfPath = Path.Combine(destDir, vmId + ".ovf");
        await File.WriteAllTextAsync(ovfPath, ovf, new UTF8Encoding(false), ct);
        return ovfPath;
    }

    /// <summary>OVA = ovf + vmdk 打 tar。旧档案先保底再替换（与 zip 导出同一策略）。</summary>
    public async Task<string> ExportOvaAsync(GrassVmPackage package, string ovaPath, string workDir, CancellationToken ct = default)
    {
        var dir = Path.Combine(workDir, "ova-" + Guid.NewGuid().ToString("N"));
        await ExportAsync(package, dir, ct);
        var tempOva = ovaPath + ".grass-tmp-" + Guid.NewGuid().ToString("N");
        var backup = ovaPath + ".grass-old-" + Guid.NewGuid().ToString("N");
        var hadOld = File.Exists(ovaPath);
        try
        {
            await TarFile.CreateFromDirectoryAsync(dir, tempOva, includeBaseDirectory: false, ct);
            if (hadOld) File.Move(ovaPath, backup);
            try { File.Move(tempOva, ovaPath); }
            catch
            {
                if (hadOld) File.Move(backup, ovaPath, overwrite: true);
                throw;
            }
        }
        finally
        {
            try { if (File.Exists(tempOva)) File.Delete(tempOva); } catch { }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
        if (hadOld) try { File.Delete(backup); } catch { }
        return ovaPath;
    }

    private static string BuildOvfXml(VmConfiguration config, string vmId,
        List<(string Href, long Size, long Capacity)> disks, List<(int CdOrdinal, string Href, long Size)> isos)
    {
        var ovfNs = XNamespace.Get("http://schemas.dmtf.org/ovf/envelope/1");
        var rasdNs = XNamespace.Get("http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData");
        var xsiNs = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");

        var refs = new XElement(ovfNs + "References");
        var diskSection = new XElement(ovfNs + "DiskSection");
        foreach (var (href, size, capacity) in disks)
        {
            var id = "vmdk" + (disks.FindIndex(d => d.Href == href) + 1);
            refs.Add(new XElement(ovfNs + "File",
                new XAttribute(ovfNs + "id", "file" + id),
                new XAttribute(ovfNs + "href", href),
                new XAttribute(ovfNs + "size", size)));
            diskSection.Add(new XElement(ovfNs + "Disk",
                new XAttribute(ovfNs + "diskId", "disk" + id),
                new XAttribute(ovfNs + "fileRef", "file" + id),
                new XAttribute(ovfNs + "capacity", capacity),
                new XAttribute(ovfNs + "capacityAllocationUnits", "byte"),
                new XAttribute(ovfNs + "format", "http://www.vmware.com/interfaces/specifications/vmdk.html#streamOptimized")));
        }
        // 光盘介质的 File 引用（导入端 ResCdDvd 按 HostResource→References 解析）。
        // id 用光驱序号（与下方 Item 的 ovf:/file/fileiso<N> 一一对应，与介质是否
        // 在场无关——按"第几张光驱"稳定编号）
        foreach (var (cdOrdinal, href, size) in isos)
        {
            refs.Add(new XElement(ovfNs + "File",
                new XAttribute(ovfNs + "id", "fileiso" + cdOrdinal),
                new XAttribute(ovfNs + "href", href),
                new XAttribute(ovfNs + "size", size)));
        }

        var vs = new XElement(ovfNs + "VirtualSystem", new XAttribute(ovfNs + "id", vmId),
            new XElement(ovfNs + "Name", config.Name),
            // 系统 Profile（固件 UEFI/BIOS、SecureBoot、TPM、芯片组/总线形态的来源）：
            // 不带它的话导入端一律按 "other"（BIOS/pc/IDE）处理——UEFI 装的客户机
            // 往返一次就变成大概率起不来，且没有任何提示
            new XElement(GvmNs + "OsProfile", new XAttribute("id", config.OsProfileId)));

        var order = 0;
        foreach (var (href, _, capacity) in disks)
        {
            var id = "vmdk" + (disks.FindIndex(d => d.Href == href) + 1);
            vs.Add(Item(rasdNs, 17, "Hard Disk " + (++order),
                new XElement(rasdNs + "HostResource", $"ovf:/disk/disk{id}"),
                new XElement(rasdNs + "VirtualQuantity", capacity)));
        }
        // CD/DVD 设备：不带 Item 的话导入端会整体丢弃光驱（往返丢设备）。
        // ResourceType 15 = CD/DVD（OVF/DMTF 生态约定：16 是 Disk Drive、17 是 Hard Disk；
        // 写 16 会被导入端当硬盘分支处理，HostResource 解析不出磁盘引用 → 设备被丢）
        // 介质引用 ovf:/file/fileiso<N>（N = 光驱序号）；介质缺失/空光驱不带 HostResource
        var cdIdx = 0;
        foreach (var cd in config.Devices.OfType<CdromDevice>())
        {
            cdIdx++;
            var hasMedium = isos.Any(i => i.CdOrdinal == cdIdx);
            if (hasMedium)
            {
                vs.Add(Item(rasdNs, 15, "CD/DVD " + cdIdx,
                    new XElement(rasdNs + "HostResource", $"ovf:/file/fileiso{cdIdx}")));
            }
            else
            {
                vs.Add(Item(rasdNs, 15, "CD/DVD " + cdIdx));
            }
        }
        // 网卡：数量与模式都要带——导入端对"没有网卡 Item"的档案会补一块默认 NAT，
        // "全部断开"的配置往返一次就变成能上网（静默违背用户的断网意图）
        var nicIdx = 0;
        foreach (var nic in config.Devices.OfType<NetworkDevice>())
        {
            nicIdx++;
            vs.Add(Item(rasdNs, 10, "Ethernet " + nicIdx,
                new XElement(GvmNs + "NetworkMode", nic.Mode.ToString())));
        }
        // USB 控制器：OVF ResourceType 21 有标准映射。只导出【启用】的——
        // 停用状态用 gvm:Usb/@enabled 表达（下方），写成启用的 Item 会被
        // 导入端原样当启用设备，往返一次就把用户关掉的 USB 悄悄打开
        if (config.Devices.OfType<UsbControllerDevice>().Any(d => d.Enabled))
            vs.Add(Item(rasdNs, 21, "USB Controller"));
        // 声卡/USB 的启停状态（OVF 没有对应资源类型）：总是写出，导入端只在
        // 元素缺失（第三方信封）时才按 Profile 默认补齐——用户关掉的设备必须
        // 保持关闭，这是"忠实保留原虚拟硬件"的一部分
        vs.Add(new XElement(GvmNs + "Usb",
            new XAttribute("enabled", config.Devices.OfType<UsbControllerDevice>().FirstOrDefault()?.Enabled ?? false)));
        vs.Add(new XElement(GvmNs + "Audio",
            new XAttribute("enabled", config.Devices.OfType<GrassCore.Config.AudioDevice>().FirstOrDefault()?.Enabled ?? false)));
        vs.Add(Item(rasdNs, 3, "Virtual CPU", new XElement(rasdNs + "VirtualQuantity", config.CpuCores)));
        vs.Add(Item(rasdNs, 4, "Memory", new XElement(rasdNs + "AllocationUnits", "MegaBytes"),
            new XElement(rasdNs + "VirtualQuantity", config.MemoryMiB)));

        // Raw/兼容设备：OVF 标准没有对应资源类型——用扩展元素（gvm:QemuArgs，每行一个参数）
        // 原样带出，再导入时回到 Raw 兼容设备。静默丢弃会让"导出→导入"的往返丢设备。
        // Unsupported 标记一起带出：否则"占位（不可用）"的设备一往返就复活成可用——
        // 用户当初被明确告知这些参数不会生效
        foreach (var raw in config.Devices.OfType<RawDevice>().Where(r => r.Arguments.Count > 0))
        {
            var argsEl = new XElement(GvmNs + "QemuArgs", string.Join("\n", raw.Arguments));
            if (raw.Unsupported)
                argsEl.Add(new XAttribute("unsupported", "true"));
            vs.Add(Item(rasdNs, 1, string.IsNullOrEmpty(raw.Label) ? "兼容设备" : raw.Label, argsEl));
        }

        var env = new XElement(ovfNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "ovf", ovfNs),
            new XAttribute(XNamespace.Xmlns + "rasd", rasdNs),
            new XAttribute(XNamespace.Xmlns + "xsi", xsiNs),
            new XAttribute(XNamespace.Xmlns + "gvm", GvmNs),
            // 出处标记：导入端只信任带此标记档案的 gvm:QemuArgs（那是我们自己的
            // 序列化格式）。第三方 OVF 里的同名元素按不可信参数处理
            new XElement(GvmNs + "Export", new XAttribute("tool", "Grass Block VM"),
                new XAttribute("version", "1.0")),
            refs, diskSection, vs);
        return env.ToString(SaveOptions.None);
    }

    private static XElement Item(XNamespace rasdNs, int resourceType, string caption, params XElement[] extra)
    {
        var item = new XElement(rasdNs + "Item",
            new XElement(rasdNs + "Caption", caption),
            new XElement(rasdNs + "ResourceType", resourceType));
        foreach (var e in extra) item.Add(e);
        return item;
    }

    /// <summary>Grass Block VM 的 OVF 扩展命名空间（Raw 兼容设备参数等非标准信息）。</summary>
    public static readonly XNamespace GvmNs = XNamespace.Get("http://grass-block.vm/ovf-ext/1");

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
