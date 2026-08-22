namespace BeamSharp.Node;

/// <summary>A node name split into its <c>alive@host</c> parts.</summary>
public readonly record struct NodeName(string Alive, string Host)
{
    public string Full => $"{Alive}@{Host}";

    /// <summary>True for a short name such as <c>foo@myhost</c>, false for <c>foo@my.host.com</c>.</summary>
    public bool IsShort => !Host.Contains('.');

    public static NodeName Parse(string name)
    {
        var at = name.IndexOf('@');
        if (at <= 0 || at == name.Length - 1)
            throw new ArgumentException($"'{name}' is not a valid node name; expected alive@host", nameof(name));
        return new NodeName(name[..at], name[(at + 1)..]);
    }

    /// <summary>
    /// The short host name, matching what <c>inet:gethostname()</c> reports and therefore what an
    /// <c>iex --sname</c> peer will expect in a node name.
    /// </summary>
    public static string LocalShortHost
    {
        get
        {
            var host = System.Net.Dns.GetHostName();
            var dot = host.IndexOf('.');
            return dot < 0 ? host : host[..dot];
        }
    }

    /// <summary>The fully qualified host name, for peers started with <c>--name</c>.</summary>
    public static string LocalLongHost => System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).HostName;

    /// <summary>Builds <c>alive@shorthost</c> for the machine this process runs on.</summary>
    public static NodeName Short(string alive) => new(alive, LocalShortHost);

    public override string ToString() => Full;
}
