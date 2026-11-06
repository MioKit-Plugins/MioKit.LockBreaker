namespace MioKit.LockBreaker.Nodes;

/// <summary>
/// 搜索组：根据本次剪贴板文件快照生成批量「查看文件占用」结果。
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

        var openNode = FindOpenFileNode();
        if (openNode is null)
            return ValueTask.CompletedTask;

        if (CreateClipboardResult(request, openNode) is { } result)
            writeOnlyResultList.Add(result);

        return ValueTask.CompletedTask;
    }

    internal static SearchResult? CreateClipboardResult(SearchRequest request, OpenFileLockWindowNode openNode)
    {
        var paths = OpenFileLockWindowNode.ResolveTargetPaths(request.Context.Clipboard.Files);
        if (paths is null)
            return null;

        return new SearchResult(request, openNode)
        {
            Title = paths.Count == 1 ? "查看文件占用（剪贴板）" : $"查看文件占用（剪贴板，{paths.Count} 项）",
            Description = string.Join("；", paths.Take(3))
                + (paths.Count > 3 ? $"；另有 {paths.Count - 3} 项" : string.Empty),
            PinMode = SortPriority.Top,
            Source = "文件占用",
            Payload = paths
        };
    }

    private static OpenFileLockWindowNode? FindOpenFileNode()
    {
        var root = MioAppContext.Current.RootNode;
        return root.GetNodeById<OpenFileLockWindowNode>(FileLockFinderConst.OpenFileLockWindowNodeId)
               ?? root.GetDescendants<OpenFileLockWindowNode>().FirstOrDefault();
    }
}
