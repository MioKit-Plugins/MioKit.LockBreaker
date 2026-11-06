using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Nodes;

[EavType(FileLockFinderConst.CheckExplorerSelectionNodeTypeId)]
[NodePalette("文件工具")]
public sealed class CheckExplorerSelectionNode : MioObject,
    IHotkeyFeature, IPinnedFeature, IUseFeature, IAliasNameFeature, ISearchableFeature,
    IInvocationPresentationFeature, IRequireTargetWindowFeature
{
    public CheckExplorerSelectionNode(string id) : base(id)
    {
    }

    public override Guid MioType => FileLockFinderConst.CheckExplorerSelectionNodeType;

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
        if (request.IsEmptyText || !this.TryMatch(request.SearchText!, out var match, out var title))
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
        var selection = ExplorerSelectionResolver.Resolve(context.Context);
        var services = FileLockFinderRegister.Instance.ComponentContext;
        if (!selection.IsSuccess)
        {
            services.Resolve<IToastService>().Show(selection.Error!, ToastLevel.Warning);
            return;
        }

        await services.Resolve<LockWindowLauncher>()
            .OpenFileLockAsync(selection.Paths, autoQuery: true, context.CancellationToken);
        context.CancellationToken.ThrowIfCancellationRequested();
        await this.IncreaseAsync();
    }
}
