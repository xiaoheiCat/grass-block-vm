using System.Diagnostics;

namespace GrassCore.Tests;

/// <summary>
/// 跨平台假 qemu-img（测试专用）：
/// - POSIX：sh 脚本；Windows：qemu-img.bat 委托 PowerShell（CI 的 windows-latest 同样可跑）。
/// - create/convert：向最后一个 .qcow2/.vmdk 参数文件写入 QCOW2 魔数 + 参数行
///   （参数行留在镜像内容里——File.Move/Copy 后信息仍随文件旅行）。
/// - commit/rebase：链维护命令。真实语义的关键一面必须建模——
///   commit 的 overlay 必须有 backing（从镜像内容取最后一条 -b）且 backing 在场；
///   rebase 的新 backing（-b）必须在场。否则"删中间层把在用 backing 删了"
///   这类断链事故在假件上永远静默通过。
/// </summary>
public static class FakeQemuImg
{
    public static string Create(string dir)
    {
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            var ps1 = Path.Combine(dir, "fake-qemu-img.ps1");
            File.WriteAllText(ps1, """
                $Rest = $args
                $target = $Rest | Where-Object { "$_" -match '\.(qcow2|vmdk)' } | Select-Object -Last 1
                # backing 可能是相对引用（快照链可移植性）——按 overlay 自身位置解析并
                # 规范化（GetFullPath 消掉 ..）：真实 qemu-img info 返回干净绝对路径
                function Resolve-Backing([string]$img, [string]$b) {
                    if ("$b" -match '^[a-zA-Z]:[\\/]' -or "$b" -match '^\\\\' -or "$b" -match '^/') { return $b }
                    return [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $img) $b))
                }
                # 从镜像自身内容提取 backing（create/rebase 都把参数行追加进去了——
                # 内容随 Move/Copy 旅行，比 sidecar 可靠）
                function Get-ContentBacking([string]$img) {
                    if (-not (Test-Path $img)) { return '' }
                    $line = [IO.File]::ReadAllLines($img) |
                        Where-Object { $_ -match '-b (\S+)' } | Select-Object -Last 1
                    if ($line -match '-b (\S+)') { return $Matches[1] }
                    return ''
                }
                if ($Rest[0] -eq 'check') { exit 0 } # 基座体检（commit 后）：假件一律通过
                if ($Rest[0] -eq 'info') {
                    # 与真实 qemu-img 一致：目标不存在 → 非零退出。严格查询路径
                    # （Delete 依赖者判定 / OVF 预检 / PlanDelete）的失败语义
                    # 必须可被测试触达，否则 catch (QemuImgException) 分支永不执行
                    if (-not (Test-Path "$target")) {
                        Write-Error "qemu-img: Could not open '$target': No such file or directory"
                        exit 1
                    }
                    $b = Get-ContentBacking "$target"
                    if ($b) {
                        $rb = Resolve-Backing $target $b
                        Write-Output ('{"full-backing-filename":"' + ($rb -replace '\\', '/') + '"}')
                    } else {
                        Write-Output '{}'
                    }
                    exit 0
                }
                if ($Rest[0] -eq 'commit') {
                    # commit：overlay 必须有 backing（内容里记录）且 backing 在场
                    $backing = Get-ContentBacking "$target"
                    if (-not $backing) {
                        Write-Error "qemu-img: '$target' does not have a backing file"
                        exit 1
                    }
                    $rbPath = Resolve-Backing $target $backing
                    if (-not (Test-Path $rbPath)) {
                        Write-Error "qemu-img: Could not open backing image '$backing'"
                        exit 1
                    }
                    # 数据合并语义（与 POSIX 版一致）：overlay 【内容】并入 backing
                    # （提交方向可被断言）；overlay 留首行 + 最新 -b 行。Latin-1 按
                    # 字节往返，魔数不被编码层改写。只并数据行、不并 -b 指针行——
                    # 真实 qemu-img commit 不把 overlay 的 backing 指针移植进 backing；
                    # 移植了的话同级目录的相对路径会让 backing "自己 backing 自己"
                    $lat = [Text.Encoding]::GetEncoding(28591)
                    $text = $lat.GetString([IO.File]::ReadAllBytes($target))
                    $lines = $text -split "`n"
                    $dataLines = $lines | Where-Object { $_ -notmatch '-b (\S+)' }
                    [IO.File]::AppendAllText($rbPath, ($dataLines -join "`n") + "`n", $lat)
                    $first = $lines[0]
                    $lastb = $lines | Where-Object { $_ -match '-b (\S+)' } | Select-Object -Last 1
                    $out = $first + "`n"
                    if ($lastb) { $out = $out + $lastb + "`n" }
                    [IO.File]::WriteAllBytes($target, $lat.GetBytes($out))
                    Add-Content -Path (Join-Path $PSScriptRoot 'chain-ops.log') -Value ($Rest -join ' ')
                    exit 0
                }
                if ($Rest[0] -eq 'rebase') {
                    # 测试注入（按本假件实例的目录判定，不污染进程全局环境变量——
                    # xUnit 并行跑其他测试类时环境变量会串台）
                    if (Test-Path (Join-Path $PSScriptRoot 'fail-rebase.marker')) {
                        Write-Error "qemu-img: simulated rebase failure"
                        exit 1
                    }
                    $bi = [Array]::IndexOf($Rest, '-b')
                    if ($bi -ge 0 -and $bi + 1 -lt $Rest.Count) {
                        $newBacking = $Rest[$bi + 1]
                        if (-not (Test-Path (Resolve-Backing $target $newBacking))) {
                            Write-Error "qemu-img: Could not open backing image '$newBacking'"
                            exit 1
                        }
                    }
                    # rebase 后 backing 指针变了：参数行追加进镜像（最新一条生效）
                    Add-Content -Path $target -Value ($Rest -join ' ')
                    Add-Content -Path (Join-Path $PSScriptRoot 'chain-ops.log') -Value ($Rest -join ' ')
                    exit 0
                }
                # 与真实 qemu-img 一致：无 -u 时 backing 必须存在
                if ($Rest[0] -eq 'create' -and ($Rest -notcontains '-u')) {
                    $bi = [Array]::IndexOf($Rest, '-b')
                    if ($bi -ge 0 -and $bi + 1 -lt $Rest.Count) {
                        $backing = $Rest[$bi + 1]
                        if (-not (Test-Path (Resolve-Backing $target $backing))) {
                            Write-Error "qemu-img: Could not open backing image '$backing'"
                            exit 1
                        }
                    }
                }
                if ($target) {
                    if ("$target" -match '\.vmdk') {
                        [IO.File]::WriteAllText($target, "# Disk DescriptorFile`nfake-vmdk")
                    } else {
                        [IO.File]::WriteAllBytes($target, [byte[]](0x51,0x46,0x49,0xFB))
                    }
                    Add-Content -Path $target -Value ($Rest -join ' ')
                }
                exit 0
                """);
            var bat = Path.Combine(dir, "qemu-img.bat");
            File.WriteAllText(bat, $"@powershell -NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" %*\r\n");
            return bat;
        }

        var sh = Path.Combine(dir, $"fake-qemu-img-{Guid.NewGuid():N}");
        File.WriteAllText(sh, """
            #!/bin/sh
            target=$(printf '%s\n' "$@" | grep -E '\.(qcow2|vmdk)' | tail -1)
            # backing 可能是相对引用（快照链可移植性）——按 overlay 自身位置解析并
            # 【规范化】（cd/pwd 消掉 ..）：真实 qemu-img info 返回干净绝对路径，
            # 调用方拿它做相等断言/存在性判断，disks/../snapshots 这种形态不该出现
            resolve() {
              b="$1"
              case "$b" in
                /*) printf '%s' "$b" ;;
                *)  d=$(dirname "$b")
                    (cd "$(dirname "$target")/$d" 2>/dev/null && printf '%s/%s' "$(pwd)" "$(basename "$b")") ;;
              esac
            }
            # 从镜像自身内容提取 backing（create/rebase 都把自己的参数行追加了进去——
            # 内容随 File.Move/Copy 一起旅行，比 sidecar 可靠）。LC_ALL=C：镜像里的
            # QCOW2 魔数（0xFB）在 UTF-8 locale 下会让 BSD sed 报 illegal byte sequence
            backing_from_content() {
              LC_ALL=C sed -n 's/.*-b \([^ ]*\).*/\1/p' "$target" | tail -1
            }
            case "$1" in
              check)
                # 基座体检（commit 后）：假件没有真实 qcow2 结构，一律通过
                exit 0;;
              info)
                # qemu-img info --output=json 子集：full-backing-filename（从镜像内容
                # 记录解析，按镜像自身位置解析相对引用）。
                # 目标不存在 → 非零退出（真实 qemu-img 行为）：严格查询的失败
                # 语义（保守依赖者/导入拒绝）必须可被测试触达
                if [ ! -f "$target" ]; then
                  echo "qemu-img: Could not open '$target': No such file or directory" >&2
                  exit 1
                fi
                b=$(backing_from_content)
                if [ -n "$b" ]; then
                  rb=$(resolve "$b")
                  printf '{"full-backing-filename":"%s"}\n' "$rb"
                else
                  printf '{}\n'
                fi
                exit 0;;
              commit)
                # commit：overlay 必须有 backing（内容里记录）且 backing 在场
                b=$(backing_from_content)
                if [ -z "$b" ]; then
                  echo "qemu-img: '$target' does not have a backing file" >&2; exit 1
                fi
                rb=$(resolve "$b")
                if [ ! -e "$rb" ]; then
                  echo "qemu-img: Could not open backing image '$b'" >&2; exit 1
                fi
                # 数据合并语义：overlay 的【内容】并入 backing（提交方向可被测试
                # 断言——检测"commit 到错误的层/方向"这类事故）；overlay 留下首行
                # （魔数）+ 最新 -b 行（后续 info/rebase 仍能沿它解析链）。
                # 只并数据行、不并 -b 指针行：真实 qemu-img commit 决不把 overlay 的
                # backing 指针移植进 backing 的元数据——移植了的话，backing 内容里的
                # 相对 -b 路径从【backing 自己的目录】解析会指回 backing 自己
                #（同级快照目录同深度），info 就报出"自己 backing 自己"的假链
                LC_ALL=C grep -av -- '-b ' "$target" >> "$rb"
                firstline=$(LC_ALL=C head -n 1 "$target")
                lastb=$(LC_ALL=C grep -a -- '-b ' "$target" | tail -1)
                { LC_ALL=C printf '%s\n' "$firstline"
                  [ -n "$lastb" ] && printf '%s\n' "$lastb"
                } > "$target.cmt-tmp"
                mv "$target.cmt-tmp" "$target"
                printf '%s\n' "$*" >> "$0.chain-ops.log"; exit 0;;
              rebase)
                # 测试注入：本假件目录里放 fail-rebase.marker → rebase 一律失败
                # （模拟中途 IO 错误，驱动 Create 的全有或全无回滚回归测试）。
                # 不用环境变量：xUnit 并行时进程级 env 会串到别的测试类
                if [ -e "$(dirname "$0")/fail-rebase.marker" ]; then
                  echo "qemu-img: simulated rebase failure" >&2; exit 1
                fi
                # 从 "$*"（单行、空格连接）提取 -b："$@" 是每参数一行，"-b 值"跨行正则永远匹配不上
                nb=$(printf '%s\n' "$*" | sed -n 's/.*-b \([^ ]*\).*/\1/p')
                if [ -n "$nb" ]; then
                  rnb=$(resolve "$nb")
                  if [ ! -e "$rnb" ]; then
                    echo "qemu-img: Could not open backing image '$nb'" >&2; exit 1
                  fi
                fi
                # rebase 后 overlay 的 backing 指针变了：把参数行追加进镜像（此后
                # backing_from_content 取最后一条 = 新 backing）
                printf '%s\n' "$*" >> "$target"
                printf '%s\n' "$*" >> "$0.chain-ops.log"; exit 0;;
            esac
            # 与真实 qemu-img 一致：无 -u 时 backing 必须已存在（否则 exit 1）。
            # 同样从 "$*" 提取（"$@" 每参数一行，跨行匹配不上）
            if [ "$1" = "create" ] && ! printf '%s\n' "$*" | grep -q ' -u '; then
              b=$(printf '%s\n' "$*" | sed -n 's/.*-b \([^ ]*\).*/\1/p')
              if [ -n "$b" ]; then
                rb=$(resolve "$b")
                if [ ! -e "$rb" ]; then
                  echo "qemu-img: Could not open backing image '$b': No such file or directory" >&2
                  exit 1
                fi
              fi
            fi
            case "$target" in
              *vmdk*) printf '# Disk DescriptorFile\nfake-vmdk' > "$target" ;;
              *)      printf 'QFI\373' > "$target" ;;
            esac
            printf '%s\n' "$*" >> "$target"
            # 显式 exit 0：上一行在异常参数形态下返回非零时，sh 会把它当退出码
            exit 0
            """);
        Process.Start("chmod", $"+x {sh}")!.WaitForExit();
        return sh;
    }
}
