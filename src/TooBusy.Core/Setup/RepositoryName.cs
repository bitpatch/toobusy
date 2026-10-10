namespace TooBusy.Core.Setup;

public static class RepositoryName
{
    static readonly string[] Hosts = ["https://github.com/", "http://github.com/", "ssh://git@github.com/", "git@github.com:"];

    // `owner/name` out of the address of a git remote; null when the remote is not a GitHub repository.
    public static string? OfRemote(string address)
    {
        var text = address.Trim();
        if (Hosts.FirstOrDefault(host => text.StartsWith(host, StringComparison.OrdinalIgnoreCase)) is not { } host)
            return null;

        text = text[host.Length..].TrimEnd('/');
        if (text.EndsWith(".git", StringComparison.Ordinal))
            text = text[..^4];
        var parts = text.Split('/');
        return parts.Length == 2 && parts.All(part => part.Length > 0) ? text : null;
    }
}
