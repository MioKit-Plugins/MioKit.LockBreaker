using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MioKit.LockBreaker.Models;
using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Views;

public abstract partial class LockQueryWindowModelBase : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private long _requestVersion;
    private bool _disposed;
    protected bool IsDisposed => _disposed;
    private bool _isTerminating;
    protected ILockOperations Operations { get; }

    /// <summary>Optional UI confirm hook. Null skips confirmation (unit tests / headless).</summary>
    public Func<IReadOnlyList<LockOwnerItem>, Task<bool>>? ConfirmTerminateAsync { get; set; }

    [ObservableProperty] private string _windowTitle = "占用查询";
    [ObservableProperty] private string _statusText = "添加目标后开始查询";
    [ObservableProperty] private string _operationFeedback = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private LockOwnerItem? _selectedItem;
    [ObservableProperty] private LockQueryState _queryState;
    [ObservableProperty] private int _itemCount;

    public bool HasItems => ItemCount > 0;
    public bool HasOperationFeedback => !string.IsNullOrWhiteSpace(OperationFeedback);
    public bool HasSelection => SelectedItem is not null;
    public bool HasDiagnostics => Diagnostics.Count > 0;
    public bool HasFailures => Diagnostics.Any(diagnostic => diagnostic.IsFailure);
    public bool HasCoverageLimitations => Diagnostics.Any(diagnostic => diagnostic.Kind == LockDiagnosticKind.CoverageLimitation);
    public string DiagnosticSummary => HasFailures ? "查看检测问题"
        : HasCoverageLimitations ? "查看扫描范围" : "查看信息提示";
    public virtual string ResourceCountHeader => "占用数";
    public string EmptyTitle => QueryState switch
    {
        LockQueryState.Loading => "正在查询占用",
        LockQueryState.Completed when HasCoverageLimitations => "当前可访问范围内未发现占用",
        LockQueryState.Completed => "未发现占用",
        LockQueryState.PartialFailure => "检测结果不完整",
        LockQueryState.Failed => "查询未完成",
        LockQueryState.Cancelled => "查询已取消",
        _ => "等待查询"
    };
    public string EmptyDescription => QueryState switch
    {
        LockQueryState.Loading => "正在检测目标，请稍候，也可以取消本次查询。",
        LockQueryState.Completed when HasCoverageLimitations => "部分进程或句柄无法检查，可在扫描范围中查看具体原因。",
        LockQueryState.Completed => "当前检测没有发现占用目标的进程。",
        LockQueryState.PartialFailure or LockQueryState.Failed => "查看检测问题中的目标与原因，然后重试。",
        LockQueryState.Cancelled => "点击查询可重新检测当前目标。",
        _ => "指定目标后，点击查询查看占用进程。"
    };

    public ObservableCollection<LockOwnerItem> Items { get; } = [];
    public ObservableCollection<LockQueryDiagnostic> Diagnostics { get; } = [];
    protected abstract string TitlePrefix { get; }
    protected abstract bool CanQuery();
    protected abstract Task<LockQueryResult> QueryCoreAsync(CancellationToken cancellationToken);

    protected LockQueryWindowModelBase(ILockOperations operations)
    {
        Operations = operations;
        Items.CollectionChanged += (_, _) =>
        {
            ItemCount = Items.Count;
            OnPropertyChanged(nameof(HasItems));
            KillAllCommand.NotifyCanExecuteChanged();
        };
        Diagnostics.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDiagnostics));
            OnPropertyChanged(nameof(HasFailures));
            OnPropertyChanged(nameof(HasCoverageLimitations));
            OnPropertyChanged(nameof(DiagnosticSummary));
            NotifyEmptyState();
        };
    }

    protected void SetTitleSuffix(string? suffix) => WindowTitle = string.IsNullOrWhiteSpace(suffix)
        ? TitlePrefix : $"{TitlePrefix} · {suffix}";

    /// <summary>Invalidates both data and in-flight callbacks when the query target changes.</summary>
    protected void ResetQuery()
    {
        if (_disposed) return;
        ++_requestVersion;
        _request?.Cancel();
        _request = null;
        _isTerminating = false;
        IsLoading = false;
        SelectedItem = null;
        Items.Clear();
        Diagnostics.Clear();
        OperationFeedback = string.Empty;
        QueryState = LockQueryState.Idle;
        StatusText = CanQuery() ? "目标已更新，点击查询开始检测" : "请先指定查询目标";
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private bool CanRefresh() => !_disposed && !IsLoading && CanQuery();

    [RelayCommand(CanExecute = nameof(CanRefresh), AllowConcurrentExecutions = true)]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _isTerminating) return;
        _request?.Cancel();
        var version = ++_requestVersion;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _request = request;
        SelectedItem = null;
        Items.Clear();
        Diagnostics.Clear();

        if (!CanQuery())
        {
            QueryState = LockQueryState.Idle;
            StatusText = "请先指定有效查询目标";
            IsLoading = false;
            _request = null;
            return;
        }

        IsLoading = true;
        QueryState = LockQueryState.Loading;
        StatusText = "正在查询占用进程…";
        try
        {
            var result = await QueryCoreAsync(request.Token);
            if (!IsCurrent(version)) return;
            request.Token.ThrowIfCancellationRequested();
            foreach (var item in result.Owners) Items.Add(item);
            foreach (var diagnostic in result.Diagnostics) Diagnostics.Add(diagnostic);
            QueryState = !result.HasFailures ? LockQueryState.Completed
                : result.CompletedTargetCount > 0 || result.Owners.Count > 0
                    ? LockQueryState.PartialFailure : LockQueryState.Failed;
            StatusText = QueryState switch
            {
                LockQueryState.Failed => "检测失败，请查看详情",
                LockQueryState.PartialFailure => $"部分检测未完成 · 已发现 {Items.Count} 个占用进程",
                _ => Items.Count == 0
                    ? HasCoverageLimitations ? "当前可访问范围内未发现占用" : "未发现占用"
                    : $"找到 {Items.Count} 个占用进程"
            };
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(version))
            {
                QueryState = LockQueryState.Cancelled;
                StatusText = "查询已取消";
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent(version))
            {
                QueryState = LockQueryState.Failed;
                StatusText = "查询失败";
                Diagnostics.Add(new(LockDiagnosticKind.EngineFailure, "UnexpectedQueryFailure", ex.Message,
                    Stage: "QueryCoreAsync"));
            }
        }
        finally
        {
            if (IsCurrent(version))
            {
                _request = null;
                IsLoading = false;
            }
        }
    }

    private bool IsCurrent(long version) => !_disposed && version == _requestVersion;

    [RelayCommand(CanExecute = nameof(IsLoading))]
    private void Cancel() => _request?.Cancel();

    private bool CanKillSelected() => !_disposed && !IsLoading && SelectedItem is { IsProtected: false };
    private bool CanKillAll() => !_disposed && !IsLoading && Items.Any(i => !i.IsProtected);

    [RelayCommand(CanExecute = nameof(CanKillSelected))]
    private async Task KillSelectedAsync(CancellationToken cancellationToken)
    {
        if (SelectedItem is null) return;
        await TerminateAsync([SelectedItem], cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanKillAll))]
    private Task KillAllAsync(CancellationToken cancellationToken) =>
        TerminateAsync(Items.Where(i => !i.IsProtected).DistinctBy(i => i.ProcessId).ToArray(), cancellationToken);

    private async Task TerminateAsync(IReadOnlyList<LockOwnerItem> targets, CancellationToken cancellationToken)
    {
        if (_disposed || IsLoading || targets.Count == 0) return;
        if (ConfirmTerminateAsync is not null &&
            !await ConfirmTerminateAsync(targets).ConfigureAwait(true))
            return;
        var version = ++_requestVersion;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _request = request;
        _isTerminating = true;
        IsLoading = true;
        OperationFeedback = string.Empty;
        StatusText = "正在结束占用进程…";
        var cancelled = false;
        try
        {
            var messages = await Operations.TerminateAsync(targets, request.Token);
            if (!IsCurrent(version)) return;
            OperationFeedback = string.Join(Environment.NewLine, messages);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            if (IsCurrent(version)) OperationFeedback = "结束操作已取消";
        }
        catch (Exception ex)
        {
            if (IsCurrent(version)) OperationFeedback = $"结束失败：{ex.Message}";
        }
        finally
        {
            if (IsCurrent(version))
            {
                _request = null;
                _isTerminating = false;
                IsLoading = false;
            }
        }
        // Keep the action feedback separate from the subsequent scan status.
        if (IsCurrent(version) && !cancelled && !request.IsCancellationRequested)
            await RefreshAsync(cancellationToken);
        else if (IsCurrent(version)) StatusText = "操作已取消，可刷新检测结果";
    }

    partial void OnSelectedItemChanged(LockOwnerItem? value)
    {
        KillSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelection));
    }
    partial void OnIsLoadingChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        KillSelectedCommand.NotifyCanExecuteChanged();
        KillAllCommand.NotifyCanExecuteChanged();
    }
    partial void OnQueryStateChanged(LockQueryState value) => NotifyEmptyState();
    private void NotifyEmptyState()
    {
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
    }
    partial void OnOperationFeedbackChanged(string value) => OnPropertyChanged(nameof(HasOperationFeedback));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_requestVersion;
        // Linked request continuations may synchronously dispose their CTS during Cancel.
        // Clearing the reference first avoids cancelling an already-disposed source twice.
        _request = null;
        _lifetime.Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
