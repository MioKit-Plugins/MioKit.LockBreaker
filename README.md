# MioKit.LockBreaker

MioKit 的占用查找与解除插件。在搜索框内快速查找并解除文件、文件夹或端口的占用进程。

## 功能

- **文件占用**：输入路径或选择文件/文件夹，列出占用进程，可结束选中或全部进程
- **资源管理器选择项**：执行「检查选中文件占用」，自动检测原目标窗口选中的全部文件和文件夹，可绑定搜索、手势、轮盘或热键
- **批量结果**：每批只扫描一次系统句柄，按进程聚合并显示它占用的目标；文件夹包含自身及内部项目
- **进程表与详情**：紧凑表格展示进程、PID、占用数量和保护状态；选中后在独立详情区域查看文件、连接与程序信息
- **端口占用**：按端口号查询 TCP/UDP（IPv4/IPv6）监听与连接进程
- **剪贴板直达**：复制一个或多个文件/文件夹后打开搜索框，预搜索生成批量「查看文件占用」结果，显示目标数量并一次检查全部目标
- **命令附着**：输入 `port-lock` 后按宿主配置的 command 触发键附着，再输入端口号即可查询
- **安全结束**：内置系统关键进程保护名单，拒绝误杀
- **搜索集成**：打开查询窗口的节点支持别名、热键、置顶与最近使用

## 用法

- 搜索「打开文件占用」或「打开端口占用」打开查询窗口
- 在资源管理器中选中一个或多个文件/文件夹，搜索「检查选中文件占用」执行；也可以从节点面板将该节点绑定到手势或轮盘
- 新节点只使用本次调用的目标资源管理器选择项；未选中、目标不是资源管理器或采集失败时提示并结束
- 文件窗口通过「管理目标」添加路径、批量选择文件/文件夹或移除目标；点击查询，运行中可取消
- 选中进程后，在右侧查看关联文件或全部端口连接；窄窗口中详情移至下方。「更多」菜单提供结束全部可管理进程
- 复制一个或多个文件/文件夹后打开搜索框，执行预搜索中的「查看文件占用（剪贴板）」结果，一并检测全部目标
- 输入 `port-lock` 附着后输入端口号（如 `8080`、`:8080`、`port 8080`）

结束进程会跳过 csrss、lsass、explorer 等系统关键进程；可管理进程使用整棵进程树结束。

搜索执行采用打开搜索框时捕获的上下文；手势和轮盘采用宿主提供的目标窗口身份和执行阶段选择项。刷新检测窗口中的现有目标，再次执行节点才替换为新的选择项。本期仅支持 Windows 资源管理器，不读取其他窗口、桌面或剪贴板作为新节点的回退目标。

窗口会分别显示未查询、查询中、未发现占用、部分失败、失败和取消状态。部分目标失效时仍保留其余结果；检测详情记录失败原因，结束进程反馈在自动刷新后保留。原生调用之间支持协作取消，取消请求不代表正在执行的原生调用已立即结束。

检测提示分为目标失败、引擎失败、扫描范围限制和进程信息缺失。全系统扫描中某个进程拒绝访问，不代表它占用了所选目标，也不代表目标查询失败；这类信息在「查看诊断」中列出。有范围限制且未发现记录时，窗口会显示「当前可访问范围内未发现占用」。进程名称或路径读取失败时，已经确认的占用关系仍以 PID 保留。

句柄扫描和元数据查询分别使用各自需要的最小访问权限。Windows 保护机制仍可能限制访问，包括已提升权限的宿主；插件不会自动提权。诊断记录 PID、失败阶段、请求权限、原始错误码及能够读取到的保护级别，未知保护级别不会被当作已确认受保护。

要求 MioKit Host 1.3.2+ / SDK 1.3.0+。

## 项目结构

```text
plugin/     # 插件源码、plugin.json
preview/    # Avalonia 预览宿主
pack.ps1    # 打包脚本
```

## 开发

```powershell
dotnet restore MioKit.LockBreaker.slnx
dotnet build MioKit.LockBreaker.slnx -c Release
dotnet test tests/MioKit.LockBreaker.Tests.csproj -c Release
```

测试包含调用快照解析、多目标归因、窗口请求取消与过时结果防护，以及辅助进程持有真实文件/目录句柄、IPv4/IPv6 TCP/UDP 监听的 Windows 集成测试。测试只操作自行创建的临时资源和辅助进程。

界面预览使用模拟数据，不执行真实占用检测或进程结束：

```powershell
dotnet run --project preview -c Release -- --scenario files --light
dotnet run --project preview -c Release -- --scenario ports --dark
dotnet run --project preview -c Release -- --gallery
```

场景参数支持 `files`、`ports`、`empty`、`partial`、`coverage`、`coverage-empty`、`error`、`loading`、`feedback`。

可将预览自身完整 ShadWindow 的模拟视觉树渲染为 PNG，并导出同名 JSON 状态、尺寸、焦点、文字对比度与命令绑定记录：

```powershell
dotnet run --project preview -c Release -- --scenario files --palette rose --light --selection long --focus focused --render preview/obj/visual-checks/v2/files-rose-light.png --width 880 --height 620
```

配色参数支持 `--palette rose|blue`，选择项支持 `--selection first|long|protected|none`，焦点支持 `--focus focused|unfocused|inactive`。`inactive` 通过预览进程自己创建的临时窗口验证失焦，不操作用户窗口。导出不读取桌面或用户窗口，不是已安装宿主的截图。自动化测试与布局导出不代替宿主验收：需在加载本构建的 MioKit 中检查实际主题、键盘焦点、搜索/手势/轮盘的目标采集，以及重启后的别名、热键和绑定保留。

打包：

```powershell
.\pack.ps1 -PackageVersion 1.0.0 -PackageId MioKit.LockBreaker
```

输出：`artifacts/MioKit.LockBreaker.<version>.nupkg`。推送 `v*` 标签（如 `v1.0.0`）后，GitHub Actions 会用同一脚本打包，并把 `.nupkg` 挂到对应 GitHub Release（不自动发布到 NuGet）。
