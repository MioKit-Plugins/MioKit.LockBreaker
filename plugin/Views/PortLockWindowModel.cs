using CommunityToolkit.Mvvm.ComponentModel;
using MioKit.LockBreaker.Models;
using MioKit.LockBreaker.Services;

namespace MioKit.LockBreaker.Views;

public partial class PortLockWindowModel : LockQueryWindowModelBase
{
    [ObservableProperty] private string _portText = string.Empty;
    public int? Port => Services.PortText.TryParsePort(PortText, out var port) ? port : null;
    protected override string TitlePrefix => "端口占用";
    public override string ResourceCountHeader => "连接数";
    public PortLockWindowModel(ILockOperations operations) : base(operations) => SetTitleSuffix(null);

    partial void OnPortTextChanged(string value)
    {
        OnPropertyChanged(nameof(Port));
        SetTitleSuffix(Port?.ToString());
        ResetQuery();
    }

    protected override bool CanQuery() => Port is not null;
    protected override Task<LockQueryResult> QueryCoreAsync(CancellationToken cancellationToken) =>
        Operations.QueryPortAsync(Port ?? throw new InvalidOperationException("端口必须在 1–65535 之间"), cancellationToken);
}
