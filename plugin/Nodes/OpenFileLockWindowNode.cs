using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Nodes;

/// <summary>
/// 普通开窗节点：打开文件占用窗口；支持热键、置顶、最近使用与别名搜索。
/// </summary>
[EavType(FileLockFinderConst.OpenFileLockWindowNodeTypeId)]
public class OpenFileLockWindowNode :
    MioObject,
    IHotkeyFeature,
    IPinnedFeature,
    IUseFeature,
    IAliasNameFeature,
    ISearchableFeature
{
    public OpenFileLockWindowNode(string id) : base(id)
    {
    }

    public override Guid MioType => FileLockFinderConst.OpenFileLockWindowNodeType;

    public ValueTask SearchAsync(
        SearchRequest request,
        IWriteOnlyResultList<SearchResult> writeOnlyResultList,
        CancellationToken cancellationToken)
    {
        if (request.IsEmptyText)
            return ValueTask.CompletedTask;

        if (!this.TryMatch(request.SearchText!, out var match, out var title))
            return ValueTask.CompletedTask;

        writeOnlyResultList.Add(new SearchResult(request, this)
        {
            Title = title,
            Description = this.GetDescription(),
            MatchResult = match!.Value,
            Source = "文件占用"
        });

        return ValueTask.CompletedTask;
    }

    public async Task InvokeAsync(InvokeContext context)
    {
        string? path = null;
        var autoQuery = false;

        if (context.SearchResult?.Payload is string payloadPath &&
            !string.IsNullOrWhiteSpace(payloadPath))
        {
            path = payloadPath;
            autoQuery = true;
        }

        LockWindowLauncher.OpenFileLock(path, autoQuery);
        await this.IncreaseAsync();
    }
}
