namespace Quail;

public class MailServerOptions
{
    public const string SectionName = "MailServer";

    public List<ListenEndpoint> Http { get; set; } = [];
    public List<ListenEndpoint> Smtp { get; set; } = [];
    public List<ListenEndpoint> Pop3 { get; set; } = [];
    public List<ListenEndpoint> Imap { get; set; } = [];
}

public class ListenEndpoint
{
    public string Host { get; set; } = "*";
    public int Port { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Ssl { get; set; } = false;
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
}
