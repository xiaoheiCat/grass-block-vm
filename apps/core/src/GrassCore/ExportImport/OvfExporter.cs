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

        var diskFiles = new List<(string Href, long Size, long Capacity)>();
        var idx = 0;
        foreach (var disk in config.Devices.OfType<DiskDevice>())
        {
            idx++;
            var src = PathPolicy.Resolve(package, disk.Path);
            if (!File.Exists(src)) continue; // 外部丢失：导出当前有效状态时跳过并在 ovf 里不引用
            var href = $"{vmId}-disk{idx}.vmdk";
            var dst = Path.Combine(destDir, href);
            await diskOps.ConvertAsync(src, dst, "vmdk", ct);
            diskFiles.Add((href, new FileInfo(dst).Length, disk.SizeBytes > 0 ? disk.SizeBytes : new FileInfo(dst).Length));
        }

        var ovf = BuildOvfXml(config, vmId, diskFiles);
        var ovfPath = Path.Combine(destDir, vmId + ".ovf");
        await File.WriteAllTextAsync(ovfPath, ovf, new UTF8Encoding(false), ct);
        return ovfPath;
    }

    /// <summary>OVA = ovf + vmdk 打 tar。</summary>
    public async Task<string> ExportOvaAsync(GrassVmPackage package, string ovaPath, string workDir, CancellationToken ct = default)
    {
        var dir = Path.Combine(workDir, "ova-" + Guid.NewGuid().ToString("N"));
        await ExportAsync(package, dir, ct);
        if (File.Exists(ovaPath)) File.Delete(ovaPath);
        await TarFile.CreateFromDirectoryAsync(dir, ovaPath, includeBaseDirectory: false, ct);
        Directory.Delete(dir, recursive: true);
        return ovaPath;
    }

    private static string BuildOvfXml(VmConfiguration config, string vmId, List<(string Href, long Size, long Capacity)> disks)
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

        var vs = new XElement(ovfNs + "VirtualSystem", new XAttribute(ovfNs + "id", vmId),
            new XElement(ovfNs + "Name", config.Name));

        var order = 0;
        foreach (var (href, _, capacity) in disks)
        {
            var id = "vmdk" + (disks.FindIndex(d => d.Href == href) + 1);
            vs.Add(Item(rasdNs, 17, "Hard Disk " + (++order),
                new XElement(rasdNs + "HostResource", $"ovf:/disk/disk{id}"),
                new XElement(rasdNs + "VirtualQuantity", capacity)));
        }
        vs.Add(Item(rasdNs, 3, "Virtual CPU", new XElement(rasdNs + "VirtualQuantity", config.CpuCores)));
        vs.Add(Item(rasdNs, 4, "Memory", new XElement(rasdNs + "AllocationUnits", "MegaBytes"),
            new XElement(rasdNs + "VirtualQuantity", config.MemoryMiB)));

        var env = new XElement(ovfNs + "Envelope",
            new XAttribute(XNamespace.Xmlns + "ovf", ovfNs),
            new XAttribute(XNamespace.Xmlns + "rasd", rasdNs),
            new XAttribute(XNamespace.Xmlns + "xsi", xsiNs),
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

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
