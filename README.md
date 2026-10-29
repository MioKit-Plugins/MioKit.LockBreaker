# MioKit.LockBreaker

MioKit 的占用查找与解除插件。在搜索框内快速查找并解除文件、文件夹或端口的占用进程。

## 功能

- **文件占用**：输入路径或选择文件/文件夹，列出占用进程，可结束选中或全部进程
- **端口占用**：按端口号查询 TCP/UDP（IPv4/IPv6）监听与连接进程
- **剪贴板直达**：复制文件/文件夹路径后打开搜索框，直接生成「查看文件占用」结果
- **命令附着**：输入 `port-lock` 后按宿主配置的 command 触发键附着，再输入端口号即可查询
- **安全结束**：内置系统关键进程保护名单，拒绝误杀
- **搜索集成**：打开查询窗口的节点支持别名、热键、置顶与最近使用

## 用法

- 搜索「打开文件占用」或「打开端口占用」打开查询窗口
- 复制路径后打开搜索框，可直接进入文件占用查询
- 输入 `port-lock` 附着后输入端口号（如 `8080`、`:8080`、`port 8080`）

结束进程会跳过 csrss、lsass、explorer 等系统关键进程；可管理进程使用整棵进程树结束。

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
```

打包：

```powershell
.\pack.ps1 -PackageVersion 1.0.0 -PackageId MioKit.LockBreaker
```

输出：`artifacts/MioKit.LockBreaker.<version>.nupkg`。推送 `v*` 标签（如 `v1.0.0`）后，GitHub Actions 会用同一脚本打包，并把 `.nupkg` 挂到对应 GitHub Release（不自动发布到 NuGet）。
