using Avalonia.Controls;
using MioKit.LockBreaker.Models;
using MioKit.LockBreaker.Services;
using MioKit.LockBreaker.Views;

namespace MioKit.LockBreaker.Preview;

internal static class PreviewScenario
{
    public static readonly string[] Names = ["files", "ports", "empty", "partial", "coverage", "coverage-empty", "error", "loading", "feedback"];

    public static Window CreateWindow(string scenario)
    {
        var model = CreateModel(scenario);
        Window window = model is PortLockWindowModel
            ? new PortLockWindow { DataContext = model }
            : new ExplorerLockWindow { DataContext = model };
        window.Opened += async (_, _) => await LoadAsync(model, scenario);
        return window;
    }

    public static LockQueryWindowModelBase CreateModel(string scenario)
    {
        if (scenario.Equals("ports", StringComparison.OrdinalIgnoreCase))
            return new PortLockWindowModel(new PreviewOperations(scenario)) { PortText = "8080" };

        var model = new ExplorerLockWindowModel(new PreviewOperations(scenario));
        model.SetTargets([
            @"C:\Workspace\项目资料\产品设计说明.docx",
            @"C:\Workspace\项目资料\截图与演示素材",
            @"D:\Projects\MioKit\build\长期归档与部署验证\包含多个目录层级及较长中文名称的构建输出目录\MioKit.LockBreaker.Integration.Preview.LongFileName.dll"
        ]);
        if (scenario.Equals("partial", StringComparison.OrdinalIgnoreCase))
            model.AddTargets([@"Z:\Unavailable\已移动的文件.zip"]);
        return model;
    }

    public static async Task LoadAsync(LockQueryWindowModelBase model, string scenario)
    {
        await model.RefreshAsync();
        if (model.Items.Count > 0)
            model.SelectedItem = model.Items[0];
        if (scenario.Equals("feedback", StringComparison.OrdinalIgnoreCase))
            model.OperationFeedback = string.Join(Environment.NewLine, Enumerable.Range(1, 12)
                .Select(index => $"预览反馈 {index}：进程已退出，已刷新占用结果。"));
    }

    private sealed class PreviewOperations(string scenario) : ILockOperations
    {
        public async Task<LockQueryResult> QueryFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
        {
            if (scenario.Equals("loading", StringComparison.OrdinalIgnoreCase))
                await Task.Delay(Timeout.Infinite, cancellationToken);
            await Task.Delay(100, cancellationToken);

            var empty = scenario.Equals("empty", StringComparison.OrdinalIgnoreCase) || scenario.Equals("coverage-empty", StringComparison.OrdinalIgnoreCase);
            var failed = scenario.Equals("error", StringComparison.OrdinalIgnoreCase);
            var partial = scenario.Equals("partial", StringComparison.OrdinalIgnoreCase);
            var coverage = scenario.StartsWith("coverage", StringComparison.OrdinalIgnoreCase);
            var diagnostics = new List<LockQueryDiagnostic>();
            if (partial)
                diagnostics.Add(new(LockDiagnosticKind.TargetFailure, "TargetNotFound", "目标已移动或当前不可访问。其余目标的结果仍可查看。", paths[^1]));
            if (failed)
                diagnostics.Add(new(LockDiagnosticKind.EngineFailure, "HandleTableUnavailable", "句柄表读取失败，未完成本次检测。", Stage: "NtQuerySystemInformation", NativeError: 5));
            if (coverage)
            {
                diagnostics.Add(new(LockDiagnosticKind.CoverageLimitation, "ProcessAccessUnavailable", "部分进程或句柄因访问限制无法检查，结果只包含可访问范围。", ProcessId: 9999, Stage: "OpenProcess", NativeError: 5));
                if (!empty)
                    diagnostics.Add(new(LockDiagnosticKind.MetadataUnavailable, "ExecutablePathUnavailable", "已发现占用，但无法读取该进程的程序路径。", ProcessId: 14822, Stage: "QueryFullProcessImageName", NativeError: 5));
            }
            string[] names = ["Code", "explorer", "dotnet", "build-worker-with-a-long-process-name", "node", "java", "devenv", "python", "msedge", "notepad", "powershell", "archive-worker", "sync-client", "test-runner"];
            var owners = names.Select((name, index) => new FileLockOwnerItem
            {
                ProcessId = index == 1 ? 4200 : 14820 + index,
                ProcessName = coverage && index == 2 ? "PID 14822" : name,
                ServiceName = index == 2 ? "MioKitPreviewService" : null,
                ExecutablePath = coverage && index == 2 ? null : index == 0 ? @"C:\Users\Administrator\AppData\Local\Programs\Microsoft VS Code\Code.exe" : $@"C:\Program Files\MioKit Preview\{name}\{name}.exe",
                TargetPaths = paths.Take(partial ? paths.Count - 1 : paths.Count).Take(index % 3 + 1).Reverse().ToArray()
            }).Cast<LockOwnerItem>().ToArray();

            return new LockQueryResult
            {
                Owners = empty || failed ? [] : owners,
                Diagnostics = diagnostics,
                CompletedTargetCount = failed ? 0 : partial ? paths.Count - 1 : paths.Count,
                TotalTargetCount = paths.Count
            };
        }

        public async Task<LockQueryResult> QueryPortAsync(int port, CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            string[] names = ["node", "svchost", "dotnet", "nginx", "java", "python", "http-worker", "dev-server", "proxy-service", "docker-proxy", "test-host", "web-api", "preview-worker", "gateway"];
            return new LockQueryResult
            {
                Owners = names.Select((name, index) => new PortLockOwnerItem
                {
                    ProcessId = 23680 + index,
                    ProcessName = name,
                    ExecutablePath = $@"C:\Program Files\MioKit Preview\{name}\{name}.exe",
                    Connections = new[]
                    {
                        new PortConnection("TCP", $"127.0.0.1:{port}", "0.0.0.0:0", "LISTEN"),
                        new PortConnection("TCP", $"127.0.0.1:{port}", "127.0.0.1:59120", "ESTABLISHED"),
                        new PortConnection("TCP6", $"[::1]:{port}", "[::]:0", "LISTEN")
                    }.Take(index % 3 + 1).ToArray()
                }).Cast<LockOwnerItem>().ToArray(),
                CompletedTargetCount = 1,
                TotalTargetCount = 1
            };
        }

        public Task<IReadOnlyList<string>> TerminateAsync(IReadOnlyList<LockOwnerItem> targets, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>([$"预览：已模拟结束 {targets.Count} 个进程，未执行实际进程操作。"]);
        }
    }
}
