using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Nodes;

/// <summary>
/// 普通开窗节点：打开端口占用窗口；支持热键、置顶、最近使用与别名搜索。
/// </summary>
[EavType(FileLockFinderConst.OpenPortLockWindowNodeTypeId)]
[NodePalette("占用查询")]
public class OpenPortLockWindowNode :
    MioObject,
    IHotkeyFeature,
    IPinnedFeature,
    IUseFeature,
    IAliasNameFeature,
    ISearchableFeature,
    IInvocationPresentationFeature
{
    public OpenPortLockWindowNode(string id) : base(id)
    {
    }

    public override Guid MioType => FileLockFinderConst.OpenPortLockWindowNodeType;

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
            Source = "端口占用"
        });

        return ValueTask.CompletedTask;
    }

    public async Task InvokeAsync(InvokeContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        int? port = null;
        var autoQuery = false;

        if (context.SearchResult?.Payload is int payloadPort)
        {
            port = payloadPort;
            autoQuery = true;
        }

        await FileLockFinderRegister.Instance.ComponentContext.Resolve<LockWindowLauncher>()
            .OpenPortLockAsync(port, autoQuery, context.CancellationToken);
        context.CancellationToken.ThrowIfCancellationRequested();
        await this.IncreaseAsync();
    }
}
