using MioKit.LockBreaker.Models;
using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Views;

public partial class PortLockWindowModel : LockQueryWindowModelBase
{
    private string _portText = string.Empty;

    public string PortText
    {
        get => _portText;
        set
        {
            if (SetProperty(ref _portText, value ?? string.Empty))
            {
                SetTitleSuffix(Services.PortText.TryParsePort(_portText, out var port) ? port.ToString() : null);
            }
        }
    }

    public int? Port => Services.PortText.TryParsePort(PortText, out var port) ? port : null;

    protected override string TitlePrefix => "端口占用";

    protected override bool CanQuery() => Port is not null;

    protected override Task<IReadOnlyList<LockOwnerItem>> QueryCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = Port ?? throw new InvalidOperationException("无效端口");
        return Task.FromResult(PortLockQuery.FindLockOwners(port));
    }
}
