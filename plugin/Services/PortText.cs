namespace MioKit.LockBreaker.Services;

internal static class PortText
{
    public static bool TryParsePort(string? text, out int port)
    {
        port = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var raw = text.Trim();
        if (raw.StartsWith("port", StringComparison.OrdinalIgnoreCase))
            raw = raw[4..].Trim();
        if (raw.StartsWith(':'))
            raw = raw[1..].Trim();

        return int.TryParse(raw, out port) && port is >= 1 and <= 65535;
    }
}
