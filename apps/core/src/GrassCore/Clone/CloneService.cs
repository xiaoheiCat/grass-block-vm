using GrassCore.Config;
using GrassCore.GrassVm;
using GrassCore.Qemu;
using GrassCore.Rpc;

namespace GrassCore.Clone;

/// <summary>
/// 克隆服务：
/// - 完整克隆 = 独立完整副本：磁盘扁平化为独立 QCOW2（脱离父链/快照），不含快照历史，
///   不复制 state.json 等本机痕迹，设备 UUID 重新生成。
/// - 链接克隆 = 必须基于某个快照创建：磁盘为指向该快照 overlay 的 QCOW2 backing overlay，
///   config 来自快照的完整副本 + cloneInfo{父包（同目录时相对路径）, 父快照 UUID}。
///   删除被依赖快照会让链接克隆失效（删除前列出受影响列表并警告，但最终允许删除）。
/// </summary>
public sealed class CloneService(TransactionalDiskOps diskOps)
{
    /// <summary>完整克隆。命名冲突时抛异常（不覆盖既有 VM）。</summary>
    public async Task<GrassVmPackage> FullCloneAsync(GrassVmPackage source, string newName, CancellationToken ct = default)
    {
        if (!source.FixedDirectoriesAreSafe() || !source.FixedFilesAreSafe())
            throw new GrassCoreException("源虚拟机包包含不受支持的符号链接或目录联接，已拒绝克隆。");
        var config = new ConfigStore(source).Load();
        string? linkedParentRoot = null;
        if (config.CloneInfo is { ParentVmPath: { Length: > 0 } parentRef })
        {
            try
            {
                // CloneInfo.ParentVmPath 允许同级父包的 ../ 相对引用，不能用
                // 普通包内 PathPolicy（它会刻意拒绝越出当前包的路径）。
                var parentPath = Path.GetFullPath(Path.IsPathRooted(parentRef)
                    ? parentRef
                    : Path.Combine(source.Path, parentRef.Replace('/', Path.DirectorySeparatorChar)));
                if (Directory.Exists(parentPath))
                {
                    var parentPackage = new GrassVmPackage(parentPath);
                    if (parentPackage.FixedDirectoriesAreSafe() && parentPackage.FixedFilesAreSafe())
                        linkedParentRoot = Path.GetFullPath(parentPackage.Path).TrimEnd(Path.DirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
                }
            }
            catch (ArgumentException) { /* 非法父引用按普通独立包处理，后续 backing 校验会拒绝 */ }
            catch (IOException) { }
        }
        var parentDir = Path.GetDirectoryName(source.Path)!;
        var target = GrassVmPackage.CreateNew(parentDir, newName);
        try
        {
            var newConfig = ConfigJson.Deserialize(ConfigJson.Serialize(config)); // 深拷贝
            newConfig.Name = newName;
            newConfig.CloneInfo = null; // 完整克隆是独立 VM，不再依赖父
            foreach (var d in newConfig.Devices) d.DeviceId = Guid.NewGuid().ToString();

            foreach (var disk in newConfig.Devices.OfType<DiskDevice>().ToList())
            {
                var srcPath = PathPolicy.Resolve(source, disk.Path);
                if (PathPolicy.IsExternal(source, disk.Path))
                {
                    // 包外磁盘保持原引用（克隆不复制包外数据盘）
                    continue;
                }
                var baseName = Path.GetFileName(disk.Path);
                var newDiskPath = Path.Combine(target.DisksPath, baseName);
                if (File.Exists(newDiskPath))
                {
                    var ext = Path.GetExtension(baseName);
                    newDiskPath = Path.Combine(target.DisksPath, disk.DeviceId + (string.IsNullOrEmpty(ext) ? ".qcow2" : ext));
                }
                // 扁平化为独立镜像（脱离一切 backing 链）
                EnsureBackingChainContained(source, srcPath, linkedParentRoot);
                await diskOps.ConvertToQcow2Async(srcPath, newDiskPath, ct);
                disk.Path = PathPolicy.NormalizeReference(target, newDiskPath);
            }
            // NVRAM 独立复制（UEFI 变量属于 VM 实例）
            var vars = Path.Combine(source.FirmwarePath, "VARS.fd");
            if (File.Exists(vars)) File.Copy(vars, Path.Combine(target.FirmwarePath, "VARS.fd"));
            // 包内介质（isovol/，OVF 导入的 CD ISO）跟随：包内相对引用在克隆包里
            // 没有对应文件 = 永远空光驱 + 设置页挂着死文件名
            CopyInternalIsos(source, target, newConfig);
            // artwork 跟随（封面是用户资产）
            if (Directory.Exists(source.ArtworkPath))
                CopyDirectory(source.ArtworkPath, target.ArtworkPath);

            new ConfigStore(target).Save(newConfig);
            return target;
        }
        catch
        {
            // 回滚清场不改写原始错误（同 OvfImporter/CreateVm 的防御）；残留
            // 骨架由 ScanLibrary 列出，换名即可重试
            try { if (Directory.Exists(target.Path)) Directory.Delete(target.Path, recursive: true); }
            catch { /* 残留骨架：换名重试或手工清理 */ }
            throw;
        }
    }

    /// <summary>链接克隆：基于指定快照。磁盘 overlay 的 backing 指向快照 overlay 文件。</summary>
    public GrassVmPackage LinkedClone(GrassVmPackage source, string snapshotUuid, string newName)
    {
        if (!source.FixedDirectoriesAreSafe() || !source.FixedFilesAreSafe())
            throw new GrassCoreException("源虚拟机包包含不受支持的符号链接或目录联接，已拒绝克隆。");
        var tree = SnapshotService.LoadTree(source);
        // 陈旧 UUID（确认框开着时快照被别处删掉）→ 产品化措辞，不裸抛
        // KeyNotFoundException（与 Restore/Delete/PlanDelete 的措辞一致）
        if (!tree.TryGet(snapshotUuid, out var snap) || snap is null)
            throw new GrassCoreException("快照不存在，请刷新列表。");
        if (Path.GetFileName(snapshotUuid) != snapshotUuid || snapshotUuid is "." or "..")
            throw new GrassCoreException("快照标识不合法。");
        var parentDir = Path.GetDirectoryName(source.Path)!;
        var target = GrassVmPackage.CreateNew(parentDir, newName);
        try
        {
            var config = ConfigJson.Deserialize(snap.FullConfigSnapshot);
            config.Name = newName;
            if (config.Devices.OfType<DiskDevice>().Any(d => PathPolicy.IsExternal(source, d.Path)))
                throw new GrassCoreException("链接克隆不能包含包外可写硬盘，请先将硬盘移入虚拟机包后再创建克隆。");
            // 设备 ID 全部换新：与完整克隆一致。DeviceId 决定派生 MAC——沿用父包 ID
            // 会让克隆与父 VM 同 MAC（"同时运行父与克隆"正是链接克隆的存在意义）。
            var idRemap = new Dictionary<string, string>();
            foreach (var dev in config.Devices)
            {
                var oldId = dev.DeviceId;
                dev.DeviceId = Guid.NewGuid().ToString();
                idRemap[oldId] = dev.DeviceId;
            }
            // 快照的冻结文件引用按旧 ID 记录：换算后再用
            var overlayRefs = snap.DiskOverlayRefs.ToDictionary(
                kv => idRemap.TryGetValue(kv.Key, out var nid) ? nid : kv.Key,
                kv => kv.Value);
            var internalDisks = config.Devices.OfType<DiskDevice>()
                .Where(d => !PathPolicy.IsExternal(source, d.Path)).ToList();
            if (internalDisks.Any(d => !overlayRefs.ContainsKey(d.DeviceId)))
                throw new GrassCoreException("快照缺少内部磁盘的冻结引用，已拒绝创建不完整的链接克隆。");
            config.CloneInfo = new CloneInfo
            {
                // 同一目录树下的父包保存相对引用（../Name.grassvm），移动整个 Library 后仍然有效；
                // 不同位置则保存绝对路径，失效时按统一重定位流程处理。
                ParentVmPath = NormalizeSiblingReference(target, source),
                ParentSnapshotUuid = snap.Uuid,
            };

            foreach (var disk in config.Devices.OfType<DiskDevice>().ToList())
            {
                if (PathPolicy.IsExternal(source, disk.Path)) continue;
                if (!overlayRefs.TryGetValue(disk.DeviceId, out var overlayRef))
                    throw new GrassCoreException("快照缺少内部磁盘的冻结引用，已拒绝创建不完整的链接克隆。");
                var snapshotDir = Path.GetFullPath(Path.Combine(source.SnapshotsPath, snap.Uuid));
                var backingFile = Path.GetFullPath(Path.Combine(snapshotDir, overlayRef.Replace('/', Path.DirectorySeparatorChar)));
                var pathCmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!backingFile.StartsWith(snapshotDir + Path.DirectorySeparatorChar, pathCmp)
                    || !File.Exists(backingFile)
                    // 文件本身之外，快照目录及其每一级父目录也必须是普通目录。
                    // 仅检查 backing 文件会被 snapshots/<uuid> 下的 junction/symlink
                    // 绕过词法 StartsWith，进而把包外文件接入链接克隆。
                    || GrassVmPackage.ContainsReparsePoint(backingFile))
                    throw new GrassCoreException("快照磁盘引用无效，已拒绝创建链接克隆。");
                var ext = Path.GetExtension(disk.Path);
                var overlayPath = Path.Combine(target.DisksPath, disk.DeviceId + (string.IsNullOrEmpty(ext) ? ".qcow2" : ext));
                // QCOW2 overlay：backing 指向父快照的 overlay（外部链）。
                // 相对引用（../Parent.grassvm/…，按 overlay 自身位置解析）——与
                // CloneInfo.ParentVmPath 的承诺一致：移动整个 Library 后链仍然有效；
                // 绝对引用在移动/改名后直接悬空且无重定位流程
                diskOps.CreateOverlay(backingFile, overlayPath, relativeBacking: true);
                disk.Path = PathPolicy.NormalizeReference(target, overlayPath);
            }
            var vars = Path.Combine(source.SnapshotsPath, snap.Uuid, "VARS.fd");
            if (File.Exists(vars))
            {
                if (GrassVmPackage.IsReparsePointOrLink(vars))
                    throw new GrassCoreException("快照固件变量文件不能是符号链接或目录联接，已拒绝创建链接克隆。");
                File.Copy(vars, Path.Combine(target.FirmwarePath, "VARS.fd"));
            }
            // 快照配置里的 CD 同样可能引用父包 isovol/ 介质：链接克隆的 config 是
            // 快照配置的深拷贝，包内相对引用会指向克隆包里不存在的文件
            CopyInternalIsos(source, target, config);

            new ConfigStore(target).Save(config);
            return target;
        }
        catch
        {
            // 同上：清场失败不顶掉原始错误
            try { if (Directory.Exists(target.Path)) Directory.Delete(target.Path, recursive: true); }
            catch { /* 残留骨架：换名重试或手工清理 */ }
            throw;
        }
    }

    /// <summary>父包引用规范化：同父目录 → 相对（../Name.grassvm）；否则绝对。</summary>
    private static string NormalizeSiblingReference(GrassVmPackage from, GrassVmPackage to)
    {
        var fromDir = Path.GetDirectoryName(from.Path)!;
        var toDir = Path.GetDirectoryName(to.Path)!;
        if (string.Equals(fromDir, toDir, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetRelativePath(from.Path, to.Path).Replace('\\', '/');
        }
        return to.Path;
    }

    /// <summary>
    /// 包内 ISO 介质（isovol/）复制进克隆包并改写引用。ISO 是只读介质、体积
    /// 可控，整份复制与克隆磁盘同标准；包外绝对引用保持原样（同包外磁盘的
    /// "不复制包外数据"语义）。源文件缺失（已被清理）则清空引用——克隆得到
    /// 空光驱，而不是指向不存在路径的死引用
    /// </summary>
    private static void CopyInternalIsos(GrassVmPackage source, GrassVmPackage target, VmConfiguration config)
    {
        foreach (var cd in config.Devices.OfType<CdromDevice>())
        {
            if (cd.IsoPath is null) continue;
            var src = PathPolicy.Resolve(source, cd.IsoPath);
            // 兼容历史配置中保存的包内绝对路径；IsInsidePackage 只接受相对
            // 引用，会把这种合法介质误判成包外而漏复制。
            if (PathPolicy.IsExternal(source, cd.IsoPath))
                continue; // 包外介质：保持原引用
            if (!File.Exists(src))
            {
                cd.IsoPath = null; // 介质已被清理：空光驱，不复制死引用
                continue;
            }
            if (GrassVmPackage.IsReparsePointOrLink(src))
                throw new GrassCoreException("虚拟机光盘介质不能是符号链接或目录联接，已拒绝克隆。");
            var dst = Path.Combine(target.Path, "isovol", cd.DeviceId + "-" + Path.GetFileName(src));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true); // 两个光驱共用同一介质：幂等覆盖
            cd.IsoPath = PathPolicy.NormalizeReference(target, dst);
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        if (GrassVmPackage.IsReparsePointOrLink(src))
            throw new GrassCoreException("虚拟机封面目录包含符号链接或目录联接，已拒绝克隆。");
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src))
        {
            if (GrassVmPackage.IsReparsePointOrLink(f))
                throw new GrassCoreException("虚拟机封面目录包含符号链接或目录联接，已拒绝克隆。");
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        }
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    private void EnsureBackingChainContained(GrassVmPackage package, string image, string? allowedExternalRoot)
    {
        var root = Path.GetFullPath(package.Path).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var current = Path.GetFullPath(image);
        for (var depth = 0; depth < 64; depth++)
        {
            if (!seen.Add(current))
                throw new GrassCoreException("源磁盘 backing 链存在循环引用，已拒绝克隆。");
            var inPackage = current.StartsWith(root, cmp)
                || (allowedExternalRoot is not null && current.StartsWith(allowedExternalRoot, cmp));
            if (!inPackage || !File.Exists(current)
                || GrassVmPackage.IsReparsePointOrLink(current))
                throw new GrassCoreException("源磁盘 backing 指向包外或链接文件，已拒绝克隆。");
            string? backing;
            try { backing = diskOps.QueryBackingFileStrict(current); }
            catch (QemuImgException ex)
            {
                throw new GrassCoreException($"无法验证源磁盘 backing 链，已拒绝克隆：{ex.Message}");
            }
            if (backing is null) return;
            current = Path.GetFullPath(backing);
        }
        throw new GrassCoreException("源磁盘 backing 链超过 64 层，已拒绝克隆。");
    }
}
