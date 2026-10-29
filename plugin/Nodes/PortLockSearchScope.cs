using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Nodes;

/// <summary>
/// command「port」附着后，解析端口号，结果交给开窗节点执行。
/// </summary>
[EavType(FileLockFinderConst.PortLockSearchScopeTypeId)]
public class PortLockSearchScope : MioObject, ISearchScopeFeature
{
    public PortLockSearchScope(string id) : base(id)
    {
        Name = "端口占用";
        Description = "附着后输入端口号，查看占用进程";
    }

    public override Guid MioType => FileLockFinderConst.PortLockSearchScopeType;

    public IReadOnlyList<string> SearchCommands { get; } = ["port-lock"];

    public Task OnAttachSearchBox(ISearchBoxWindow searchBoxWindow) => Task.CompletedTask;

    public Task OnDetachSearchBox(ISearchBoxWindow searchBoxWindow) => Task.CompletedTask;

    public Task InvokeAsync(InvokeContext context) =>
        MioIoc.Resolve<ISearchBoxWindow>().SetAttachedPanelAsync(this);

    public ValueTask SearchAsync(
        SearchRequest request,
        IWriteOnlyResultList<SearchResult> writeOnlyResultList,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.IsEmptyText)
            return ValueTask.CompletedTask;

        if (!PortText.TryParsePort(request.SearchText, out var port))
            return ValueTask.CompletedTask;

        var openNode = FindOpenPortNode();
        if (openNode is null)
            return ValueTask.CompletedTask;

        writeOnlyResultList.Add(new SearchResult(request, openNode)
        {
            Title = $"查看端口 {port} 占用",
            Description = $"TCP/UDP :{port}",
            PinMode = SortPriority.Top,
            Source = "端口占用",
            Payload = port
        });

        return ValueTask.CompletedTask;
    }

    private static OpenPortLockWindowNode? FindOpenPortNode()
    {
        var root = MioAppContext.Current.RootNode;
        return root.GetNodeById<OpenPortLockWindowNode>(FileLockFinderConst.OpenPortLockWindowNodeId)
               ?? root.GetDescendants<OpenPortLockWindowNode>().FirstOrDefault();
    }
}
