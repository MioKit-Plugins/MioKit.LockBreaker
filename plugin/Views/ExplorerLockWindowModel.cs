using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MioKit.LockBreaker.Models;
using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Views;

public partial class ExplorerLockWindowModel : LockQueryWindowModelBase
{
    private bool _hadTargets;
    [ObservableProperty] private string _pathInput = string.Empty;
    public ObservableCollection<string> TargetPaths { get; } = [];
    public bool HasTargets => TargetPaths.Count > 0;
    public string TargetSummary => HasTargets ? $"已选择 {TargetPaths.Count} 项" : "尚未指定文件或文件夹";
    protected override string TitlePrefix => "文件占用";
    public override string ResourceCountHeader => "目标数";

    public ExplorerLockWindowModel(ILockOperations operations) : base(operations)
    {
        SetTitleSuffix(null);
        TargetPaths.CollectionChanged += (_, _) =>
        {
            if (_hadTargets != HasTargets)
            {
                _hadTargets = HasTargets;
                OnPropertyChanged(nameof(HasTargets));
            }
            OnPropertyChanged(nameof(TargetSummary));
            SetTitleSuffix(HasTargets ? $"{TargetPaths.Count} 项" : null);
            ResetQuery();
        };
    }

    public void SetTargets(IEnumerable<string> paths)
    {
        if (IsDisposed) return;
        var targets = CleanPaths(paths).ToArray();
        TargetPaths.Clear();
        foreach (var path in targets) TargetPaths.Add(path);
        PathInput = string.Empty;
    }

    public void AddTargets(IEnumerable<string> paths)
    {
        if (IsDisposed) return;
        foreach (var path in CleanPaths(paths))
            if (!TargetPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) TargetPaths.Add(path);
    }

    private static IEnumerable<string> CleanPaths(IEnumerable<string> paths) => paths
        .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim().Trim('"'))
        .Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    [RelayCommand]
    private void AddPath()
    {
        AddTargets([PathInput]);
        PathInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveTarget(string path) => TargetPaths.Remove(path);

    protected override bool CanQuery() => HasTargets;
    protected override Task<LockQueryResult> QueryCoreAsync(CancellationToken cancellationToken) =>
        Operations.QueryFilesAsync(TargetPaths.ToArray(), cancellationToken);
}
