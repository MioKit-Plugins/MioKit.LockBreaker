using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MioKit.LockBreaker.Models;
using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Views;

public abstract partial class LockQueryWindowModelBase : ObservableObject
{
    [ObservableProperty] private string _windowTitle = "占用查询";

    [ObservableProperty] private string _statusText = string.Empty;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private LockOwnerItem? _selectedItem;

    [ObservableProperty] private bool _autoQueryOnLoad;

    [ObservableProperty] private bool _hasItems;

    [ObservableProperty] private int _itemCount;

    public ObservableCollection<LockOwnerItem> Items { get; } = [];

    protected abstract string TitlePrefix { get; }

    protected LockQueryWindowModelBase()
    {
        Items.CollectionChanged += (_, _) => SyncItemStats();
    }

    private void SyncItemStats()
    {
        ItemCount = Items.Count;
        HasItems = Items.Count > 0;
    }

    protected abstract Task<IReadOnlyList<LockOwnerItem>> QueryCoreAsync(CancellationToken cancellationToken);

    protected abstract bool CanQuery();

    protected void SetTitleSuffix(string? suffix)
    {
        WindowTitle = string.IsNullOrWhiteSpace(suffix)
            ? TitlePrefix
            : $"{TitlePrefix} - {suffix}";
    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!CanQuery())
        {
            StatusText = "请先指定有效目标";
            Items.Clear();
            return;
        }

        IsLoading = true;
        StatusText = "正在查询占用进程…";
        Items.Clear();

        try
        {
            var results = await Task.Run(
                async () => await QueryCoreAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(true);

            Items.Clear();
            foreach (var item in results)
                Items.Add(item);

            SyncItemStats();
            StatusText = Items.Count == 0
                ? "未发现占用"
                : $"找到 {Items.Count} 条占用记录";
        }
        catch (OperationCanceledException)
        {
            StatusText = "查询已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"查询失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanKillSelected))]
    private async Task KillSelectedAsync()
    {
        if (SelectedItem is null)
            return;

        IsLoading = true;
        try
        {
            var message = await Task.Run(() => ProcessTerminator.Kill(SelectedItem.ProcessId, SelectedItem.ProcessName))
                .ConfigureAwait(true);
            StatusText = message;
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanKillSelected() =>
        SelectedItem is not null &&
        !IsLoading &&
        !ProcessTerminator.IsUnmanageable(SelectedItem.ProcessId, SelectedItem.ProcessName);

    [RelayCommand(CanExecute = nameof(CanKillAll))]
    private async Task KillAllAsync()
    {
        if (Items.Count == 0)
            return;

        IsLoading = true;
        try
        {
            var targets = Items
                .Where(i => !ProcessTerminator.IsUnmanageable(i.ProcessId, i.ProcessName))
                .GroupBy(i => i.ProcessId)
                .Select(g => (g.Key, g.First().ProcessName))
                .ToList();

            if (targets.Count == 0)
            {
                StatusText = "没有可结束的进程";
                return;
            }

            var messages = await Task.Run(() => ProcessTerminator.KillMany(targets)).ConfigureAwait(true);
            StatusText = string.Join("；", messages.Take(3)) + (messages.Count > 3 ? "…" : string.Empty);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanKillAll() =>
        !IsLoading &&
        Items.Any(i => !ProcessTerminator.IsUnmanageable(i.ProcessId, i.ProcessName));

    partial void OnSelectedItemChanged(LockOwnerItem? value) => KillSelectedCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadingChanged(bool value)
    {
        KillSelectedCommand.NotifyCanExecuteChanged();
        KillAllCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }
}