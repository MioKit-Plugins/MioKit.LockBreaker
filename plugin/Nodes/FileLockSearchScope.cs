namespace MioKit.LockBreaker.Nodes;

/// <summary>
/// 搜索组：剪贴板有文件时，用第一个路径生成「查看文件占用」结果。
/// </summary>
[EavType(FileLockFinderConst.FileLockSearchScopeTypeId)]
public class FileLockSearchScope : MioObject, ISearchableFeature
{
    public FileLockSearchScope(string id) : base(id)
    {
        Name = "文件占用";
        Description = "根据剪贴板文件查看占用进程";
    }

    public override Guid MioType => FileLockFinderConst.FileLockSearchScopeType;

    public ValueTask SearchAsync(
        SearchRequest request,
        IWriteOnlyResultList<SearchResult> writeOnlyResultList,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var files = request.Context.Clipboard.Files;
        if (files is null || files.Count == 0)
            return ValueTask.CompletedTask;

        var path = files[0];
        if (string.IsNullOrWhiteSpace(path))
            return ValueTask.CompletedTask;

        var openNode = FindOpenFileNode();
        if (openNode is null)
            return ValueTask.CompletedTask;

        writeOnlyResultList.Add(new SearchResult(request, openNode)
        {
            Title = "查看文件占用（剪贴板）",
            Description = path,
            PinMode = SortPriority.Top,
            Source = "文件占用",
            Payload = path
        });

        return ValueTask.CompletedTask;
    }

    private static OpenFileLockWindowNode? FindOpenFileNode()
    {
        var root = MioAppContext.Current.RootNode;
        return root.GetNodeById<OpenFileLockWindowNode>(FileLockFinderConst.OpenFileLockWindowNodeId)
               ?? root.GetDescendants<OpenFileLockWindowNode>().FirstOrDefault();
    }
}
