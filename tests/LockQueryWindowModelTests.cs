using MioKit.LockBreaker.Models;
using MioKit.LockBreaker.Services;
using MioKit.LockBreaker.Views;
using Xunit;

namespace MioKit.LockBreaker.Tests;

public sealed class LockQueryWindowModelTests
{
    [Fact]
    public async Task ReplacingTargetsCancelsPreviousQueryAndIgnoresItsLateResult()
    {
        var oldResult = new TaskCompletionSource<LockQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        var operations = new FakeOperations
        {
            Files = (paths, token) =>
            {
                if (paths[0] == "old") { oldToken = token; return oldResult.Task; }
                return Task.FromResult(Result(22));
            }
        };
        using var model = new ExplorerLockWindowModel(operations);
        model.SetTargets(["old"]);
        var oldQuery = model.RefreshAsync();
        model.SetTargets(["new"]);
        Assert.True(oldToken.IsCancellationRequested);
        await model.RefreshAsync();
        oldResult.SetResult(Result(11));
        await oldQuery;

        Assert.Equal(22, Assert.Single(model.Items).ProcessId);
        Assert.Equal(LockQueryState.Completed, model.QueryState);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task ConsecutiveRefreshesCannotBeOverwrittenByAnEarlierCompletion()
    {
        var first = new TaskCompletionSource<LockQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var operations = new FakeOperations { Files = (_, _) => ++calls == 1 ? first.Task : Task.FromResult(Result(22)) };
        using var model = new ExplorerLockWindowModel(operations);
        model.SetTargets(["file"]);
        var pending = model.RefreshAsync();
        await model.RefreshAsync();
        first.SetResult(Result(11));
        await pending;

        Assert.Equal(22, Assert.Single(model.Items).ProcessId);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task ClosingTheModelCancelsAndPreventsLateStateUpdates()
    {
        var pending = new TaskCompletionSource<LockQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var operations = new FakeOperations { Files = (_, ct) => { token = ct; return pending.Task; } };
        var model = new ExplorerLockWindowModel(operations);
        model.SetTargets(["file"]);
        var query = model.RefreshAsync();
        model.Dispose();
        var status = model.StatusText;
        Assert.True(token.IsCancellationRequested);
        pending.SetResult(Result(11));
        await query;
        Assert.Empty(model.Items);
        Assert.Equal(status, model.StatusText);
        model.Dispose();
    }

    [Fact]
    public async Task ClosingWithASynchronousCancellationContinuationDoesNotCancelADisposedSource()
    {
        var completion = new TaskCompletionSource<LockQueryResult>();
        var model = new ExplorerLockWindowModel(new FakeOperations
        {
            Files = (_, token) =>
            {
                token.Register(() => completion.SetCanceled(token));
                return completion.Task;
            }
        });
        model.SetTargets(["file"]);
        var previousContext = SynchronizationContext.Current;
        Task pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            pending = model.RefreshAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

        Assert.Null(Record.Exception(model.Dispose));
        await pending;
        model.AddTargets(["late picker result"]);
        Assert.Equal(new[] { "file" }, model.TargetPaths);
    }

    [Fact]
    public async Task CancelIsADistinctEmptyStateAndAllowsRetry()
    {
        var operations = new FakeOperations
        {
            Files = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Result(); }
        };
        using var model = new ExplorerLockWindowModel(operations);
        model.SetTargets(["file"]);
        var query = model.RefreshAsync();
        Assert.True(model.CancelCommand.CanExecute(null));
        model.CancelCommand.Execute(null);
        await query;
        Assert.Equal(LockQueryState.Cancelled, model.QueryState);
        Assert.Equal("查询已取消", model.EmptyTitle);
        Assert.False(model.IsLoading);
        Assert.True(model.RefreshCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(0, LockQueryState.Failed)]
    [InlineData(1, LockQueryState.PartialFailure)]
    public async Task DiagnosticResultsAreNotPresentedAsNoLocks(int completed, LockQueryState state)
    {
        var operations = new FakeOperations
        {
            Files = (_, _) => Task.FromResult(new LockQueryResult
            {
                TotalTargetCount = 2, CompletedTargetCount = completed,
                Diagnostics = [new(LockDiagnosticKind.TargetFailure, "TargetAccessDenied", "无法读取目标", TargetPath: "b", NativeError: 5)]
            })
        };
        using var model = new ExplorerLockWindowModel(operations);
        model.SetTargets(["a", "b"]);
        await model.RefreshAsync();
        Assert.Equal(state, model.QueryState);
        Assert.True(model.HasDiagnostics);
        Assert.DoesNotContain("未发现占用", model.StatusText);
    }

    [Fact]
    public async Task UnrelatedProcessAccessLimitsDoNotMarkASuccessfulQueryAsFailed()
    {
        using var model = new ExplorerLockWindowModel(new FakeOperations
        {
            Files = (_, _) => Task.FromResult(new LockQueryResult
            {
                Owners = [Owner(11)], CompletedTargetCount = 1, TotalTargetCount = 1,
                Diagnostics = [new(LockDiagnosticKind.CoverageLimitation, "ProcessAccessDenied", "无法访问其他进程",
                    ProcessId: 999, Stage: "OpenProcess", RequestedAccess: 0x40, NativeError: 5)]
            })
        });
        model.SetTargets(["file"]);
        await model.RefreshAsync();

        Assert.Equal(LockQueryState.Completed, model.QueryState);
        Assert.Equal(11, Assert.Single(model.Items).ProcessId);
        Assert.False(model.HasFailures);
        Assert.True(model.HasCoverageLimitations);
        Assert.True(model.HasDiagnostics);
        Assert.DoesNotContain("未完成", model.StatusText);
        var limitation = Assert.Single(model.Diagnostics);
        Assert.Contains("PID 999", limitation.ContextText);
        Assert.Contains("0x40", limitation.ContextText);
    }

    [Fact]
    public async Task EmptyResultsWithCoverageLimitsDescribeOnlyTheAccessibleRange()
    {
        using var model = new ExplorerLockWindowModel(new FakeOperations
        {
            Files = (_, _) => Task.FromResult(new LockQueryResult
            {
                CompletedTargetCount = 1, TotalTargetCount = 1,
                Diagnostics = [new(LockDiagnosticKind.CoverageLimitation, "HandleNameTimeout", "读取句柄名称超时", ProcessId: 999)]
            })
        });
        model.SetTargets(["file"]);
        await model.RefreshAsync();

        Assert.Equal(LockQueryState.Completed, model.QueryState);
        Assert.Equal("当前可访问范围内未发现占用", model.EmptyTitle);
        Assert.Equal(model.EmptyTitle, model.StatusText);
        Assert.False(model.HasFailures);
    }

    [Fact]
    public async Task MetadataFailureKeepsTheConfirmedOwnerAndDoesNotImplyIncompleteScanning()
    {
        using var model = new ExplorerLockWindowModel(new FakeOperations
        {
            Files = (_, _) => Task.FromResult(new LockQueryResult
            {
                Owners = [new FileLockOwnerItem { ProcessId = 11, ProcessName = "PID 11", TargetPaths = ["file"] }],
                CompletedTargetCount = 1, TotalTargetCount = 1,
                Diagnostics = [new(LockDiagnosticKind.MetadataUnavailable, "ProcessImageUnavailable", "进程信息不可用", ProcessId: 11)]
            })
        });
        model.SetTargets(["file"]);
        await model.RefreshAsync();

        Assert.Equal(LockQueryState.Completed, model.QueryState);
        Assert.Equal("PID 11", Assert.Single(model.Items).ProcessName);
        Assert.False(model.HasFailures);
        Assert.False(model.HasCoverageLimitations);
    }

    [Theory]
    [InlineData(LockDiagnosticKind.TargetFailure)]
    [InlineData(LockDiagnosticKind.EngineFailure)]
    public async Task TrueFailuresRemainVisibleWhenThereAreAlsoCoverageLimits(LockDiagnosticKind kind)
    {
        using var model = new ExplorerLockWindowModel(new FakeOperations
        {
            Files = (_, _) => Task.FromResult(new LockQueryResult
            {
                Owners = [Owner(11)], CompletedTargetCount = 1, TotalTargetCount = 2,
                Diagnostics = [
                    new(LockDiagnosticKind.CoverageLimitation, "ProcessAccessDenied", "进程受限", ProcessId: 999),
                    new(kind, "ActualFailure", "实际目标或引擎故障")
                ]
            })
        });
        model.SetTargets(["one", "two"]);
        await model.RefreshAsync();

        Assert.Equal(LockQueryState.PartialFailure, model.QueryState);
        Assert.True(model.HasFailures);
        Assert.True(model.HasCoverageLimitations);
        Assert.Single(model.Items);
        Assert.Contains("未完成", model.StatusText);
    }

    [Fact]
    public async Task EditingTargetsClearsPriorDiagnosticFlagsAndSelectionDetails()
    {
        using var model = new ExplorerLockWindowModel(new FakeOperations
        {
            Files = (_, _) => Task.FromResult(new LockQueryResult
            {
                Owners = [Owner(11)], CompletedTargetCount = 1, TotalTargetCount = 1,
                Diagnostics = [new(LockDiagnosticKind.CoverageLimitation, "ProcessAccessDenied", "进程受限", ProcessId: 999)]
            })
        });
        model.SetTargets(["one"]);
        await model.RefreshAsync();
        model.SelectedItem = model.Items[0];
        Assert.True(model.HasSelection);
        model.SetTargets(["two"]);

        Assert.False(model.HasSelection);
        Assert.False(model.HasDiagnostics);
        Assert.False(model.HasFailures);
        Assert.False(model.HasCoverageLimitations);
        Assert.Empty(model.Diagnostics);
    }

    [Fact]
    public async Task EndingAProcessKeepsFeedbackAfterRefreshing()
    {
        var operations = new FakeOperations
        {
            Files = (_, _) => Task.FromResult(Result(11)),
            Terminate = (_, _) => Task.FromResult<IReadOnlyList<string>>(["已结束：test (PID 11)"])
        };
        using var model = new ExplorerLockWindowModel(operations);
        model.SetTargets(["file"]);
        await model.RefreshAsync();
        model.SelectedItem = model.Items[0];
        await model.KillSelectedCommand.ExecuteAsync(null);
        Assert.Contains("已结束", model.OperationFeedback);
        Assert.True(model.HasOperationFeedback);
        Assert.Equal(LockQueryState.Completed, model.QueryState);
        Assert.Null(model.SelectedItem);
    }

    [Fact]
    public async Task EndingAllDeduplicatesPidAndSkipsProtectedProcesses()
    {
        IReadOnlyList<LockOwnerItem>? terminated = null;
        var operations = new FakeOperations
        {
            Terminate = (targets, _) => { terminated = targets; return Task.FromResult<IReadOnlyList<string>>(["done"]); }
        };
        using var model = new ExplorerLockWindowModel(operations);
        model.SetTargets(["file"]);
        model.Items.Add(Owner(11));
        model.Items.Add(Owner(11));
        model.Items.Add(new FileLockOwnerItem { ProcessId = 22, ProcessName = "explorer" });
        Assert.True(model.KillAllCommand.CanExecute(null));
        await model.KillAllCommand.ExecuteAsync(null);
        Assert.Equal(11, Assert.Single(terminated!).ProcessId);
        Assert.False(model.KillAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task AReplacedTerminationCannotWriteFeedbackOrRefreshTheNewTarget()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken actionToken = default;
        var queryCount = 0;
        var operations = new FakeOperations
        {
            Files = (_, _) => { queryCount++; return Task.FromResult(Result(22)); },
            Terminate = (_, token) => { actionToken = token; return pending.Task; }
        };
        using var model = new ExplorerLockWindowModel(operations);
        model.SetTargets(["old"]);
        model.Items.Add(Owner(11));
        model.SelectedItem = model.Items[0];
        var action = model.KillSelectedCommand.ExecuteAsync(null);
        model.SetTargets(["new"]);
        Assert.True(actionToken.IsCancellationRequested);
        await model.RefreshAsync();
        pending.SetResult(["old feedback"]);
        await action;
        Assert.Equal(1, queryCount);
        Assert.Equal(22, Assert.Single(model.Items).ProcessId);
        Assert.False(model.HasOperationFeedback);
    }

    [Fact]
    public async Task HostCancellationIsPresentedAsCancellationEvenWithoutCallerCancellation()
    {
        using var hostCancellation = new CancellationTokenSource();
        hostCancellation.Cancel();
        using var model = new ExplorerLockWindowModel(new FakeOperations
        {
            Files = (_, _) => Task.FromCanceled<LockQueryResult>(hostCancellation.Token)
        });
        model.SetTargets(["file"]);
        await model.RefreshAsync();
        Assert.Equal(LockQueryState.Cancelled, model.QueryState);
    }

    [Fact]
    public async Task HostCancellationOfTerminationDoesNotStartAnotherManagedQuery()
    {
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        var queries = 0;
        using var model = new ExplorerLockWindowModel(new FakeOperations
        {
            Files = (_, _) => { queries++; return Task.FromResult(Result()); },
            Terminate = (_, _) => Task.FromCanceled<IReadOnlyList<string>>(stopped.Token)
        });
        model.SetTargets(["file"]);
        model.Items.Add(Owner(11));
        model.SelectedItem = model.Items[0];
        await model.KillSelectedCommand.ExecuteAsync(null);
        Assert.Equal(0, queries);
        Assert.Contains("取消", model.OperationFeedback);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task PortInputValidationDoesNotPerformIoAndInvalidatesOldResults()
    {
        var calls = 0;
        using var model = new PortLockWindowModel(new FakeOperations
        {
            Port = (_, _) => { calls++; return Task.FromResult(Result(11)); }
        });
        model.PortText = "65536";
        Assert.False(model.RefreshCommand.CanExecute(null));
        await model.RefreshAsync();
        Assert.Equal(0, calls);
        model.PortText = "8080";
        Assert.True(model.RefreshCommand.CanExecute(null));
        await model.RefreshAsync();
        Assert.Single(model.Items);
        model.PortText = "abc";
        Assert.Empty(model.Items);
        Assert.Equal(LockQueryState.Idle, model.QueryState);
    }

    [Fact]
    public async Task EditingTargetsClearsSelectionAndCommandStateWithoutAQuery()
    {
        using var model = new ExplorerLockWindowModel(new FakeOperations { Files = (_, _) => Task.FromResult(Result(11)) });
        model.SetTargets(["one"]);
        await model.RefreshAsync();
        model.SelectedItem = model.Items[0];
        Assert.True(model.KillSelectedCommand.CanExecute(null));
        model.AddTargets(["two", "TWO", "  "]);
        Assert.Equal(2, model.TargetPaths.Count);
        Assert.Empty(model.Items);
        Assert.Null(model.SelectedItem);
        Assert.False(model.KillSelectedCommand.CanExecute(null));
        Assert.False(model.KillAllCommand.CanExecute(null));
    }

    private static FileLockOwnerItem Owner(int pid) => new() { ProcessId = pid, ProcessName = "test", TargetPaths = ["file"] };
    private static LockQueryResult Result(params int[] pids) => new()
    {
        Owners = pids.Select(pid => (LockOwnerItem)Owner(pid)).ToArray(), CompletedTargetCount = 1, TotalTargetCount = 1
    };

    private sealed class FakeOperations : ILockOperations
    {
        public Func<IReadOnlyList<string>, CancellationToken, Task<LockQueryResult>> Files { get; init; } = (_, _) => Task.FromResult(Result());
        public Func<int, CancellationToken, Task<LockQueryResult>> Port { get; init; } = (_, _) => Task.FromResult(Result());
        public Func<IReadOnlyList<LockOwnerItem>, CancellationToken, Task<IReadOnlyList<string>>> Terminate { get; init; } = (_, _) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<LockQueryResult> QueryFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) => Files(paths, cancellationToken);
        public Task<LockQueryResult> QueryPortAsync(int port, CancellationToken cancellationToken) => Port(port, cancellationToken);
        public Task<IReadOnlyList<string>> TerminateAsync(IReadOnlyList<LockOwnerItem> targets, CancellationToken cancellationToken) => Terminate(targets, cancellationToken);
    }
}
