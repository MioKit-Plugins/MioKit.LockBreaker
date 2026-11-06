using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Nodes;

/// <summary>
/// 普通开窗节点：打开文件占用窗口；支持热键、置顶、最近使用与别名搜索。
/// </summary>
[EavType(FileLockFinderConst.OpenFileLockWindowNodeTypeId)]
[NodePalette("占用查询")]
public class OpenFileLockWindowNode :
    MioObject,
    IHotkeyFeature,
    IPinnedFeature,
    IUseFeature,
    IAliasNameFeature,
    ISearchableFeature,
    IInvocationPresentationFeature
{
    public OpenFileLockWindowNode(string id) : base(id)
    {
    }

    public override Guid MioType => FileLockFinderConst.OpenFileLockWindowNodeType;

    public InvocationPresentation GetPresentation(InvocationPresentationRequest request) => new()
    {
        Interaction = InvocationInteraction.CompleteBeforeExecution
    };

    public ValueTask SearchAsync(
        SearchRequest request,
        IWriteOnlyResultList<SearchResult> writeOnlyResultList,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        context.CancellationToken.ThrowIfCancellationRequested();
        var paths = ResolveTargetPaths(context.SearchResult?.Payload);

        await FileLockFinderRegister.Instance.ComponentContext.Resolve<LockWindowLauncher>()
            .OpenFileLockAsync(paths, autoQuery: paths is not null, context.CancellationToken);
        context.CancellationToken.ThrowIfCancellationRequested();
        await this.IncreaseAsync();
    }

    internal static IReadOnlyList<string>? ResolveTargetPaths(object? payload)
    {
        IEnumerable<string> paths = payload switch
        {
            string path => [path],
            IReadOnlyList<string> batch => batch,
            _ => []
        };
        var targets = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return targets.Length == 0 ? null : targets;
    }
}
