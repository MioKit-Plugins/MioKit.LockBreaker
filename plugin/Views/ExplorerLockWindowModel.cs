using MioKit.LockBreaker.Models;
using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Views;

public partial class ExplorerLockWindowModel : LockQueryWindowModelBase
{
    private string _targetPath = string.Empty;

    public string TargetPath
    {
        get => _targetPath;
        set
        {
            if (SetProperty(ref _targetPath, value ?? string.Empty))
            {
                var name = string.IsNullOrWhiteSpace(_targetPath)
                    ? null
                    : Path.GetFileName(_targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                SetTitleSuffix(name);
            }
        }
    }

    protected override string TitlePrefix => "文件占用";

    protected override bool CanQuery() =>
        !string.IsNullOrWhiteSpace(TargetPath) &&
        (File.Exists(TargetPath) || Directory.Exists(TargetPath));

    protected override Task<IReadOnlyList<LockOwnerItem>> QueryCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = TargetPath;
        return Task.FromResult(FileLockQuery.FindLockOwners(path));
    }
}
