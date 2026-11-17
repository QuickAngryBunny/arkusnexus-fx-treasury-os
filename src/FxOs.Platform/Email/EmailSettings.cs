namespace FxOs.Platform.Email;

/// <summary>Configuration section <c>Email</c>.</summary>
public sealed class EmailSettings
{
    public const string Section = "Email";

    /// <summary>
    /// Who sends: "firebase" (the Trigger Email extension, needs Google credentials) or "smtp" (any SMTP server, e.g.
    /// Gmail with an app password; works on hosts without Google credentials, such as Railway).
    /// </summary>
    public string Transport { get; set; } = EmailTransport.Firebase;

    public FirebaseSettings Firebase { get; set; } = new();

    public SmtpSettings Smtp { get; set; } = new();

    /// <summary>Whether the chosen transport has what it needs to send.</summary>
    public bool IsConfigured => Transport == EmailTransport.Smtp ? Smtp.IsConfigured : Firebase.IsConfigured;

    public DispatcherSettings Dispatcher { get; set; } = new();

    /// <summary>
    /// Addresses the dispatcher may send to: exact addresses or <c>*@domain</c>. Local runs write to the real Firebase
    /// project, so keep this to test inboxes. Anything else is marked failed without reaching Firebase.
    /// </summary>
    public string[] AllowedRecipients { get; set; } = [];

    /// <summary>Lifts the allowlist. Only for the environment that sends to real users, after the DevOps hand-off.</summary>
    public bool AllowAllRecipients { get; set; }

    public bool IsAllowed(string email)
    {
        if (AllowAllRecipients)
        {
            return true;
        }

        var address = email.Trim().ToLowerInvariant();
        var at = address.LastIndexOf('@');
        var domain = at >= 0 ? address[at..] : null;
        foreach (var raw in AllowedRecipients)
        {
            var rule = raw.Trim().ToLowerInvariant();
            if (rule.Length == 0)
            {
                continue;
            }

            if (rule == address || (domain is not null && rule.StartsWith('*') && rule[1..] == domain))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class FirebaseSettings
{
    /// <summary>Firebase / Google Cloud project id that runs the Trigger Email extension.</summary>
    public string ProjectId { get; set; } = "";

    /// <summary>
    /// Path to the service-account JSON key, kept outside the repo (Key Vault after the hand-off). Empty uses
    /// Application Default Credentials (GOOGLE_APPLICATION_CREDENTIALS).
    /// </summary>
    public string CredentialsFile { get; set; } = "";

    /// <summary>The collection the extension watches.</summary>
    public string Collection { get; set; } = "mail";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectId);
}

public static class EmailTransport
{
    public const string Firebase = "firebase";
    public const string Smtp = "smtp";
}

public sealed class SmtpSettings
{
    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    /// <summary>"starttls" (port 587, the default), "ssl" (port 465) or "none" (local test servers only).</summary>
    public string Security { get; set; } = "starttls";

    /// <summary>Empty for servers that don't ask for a login.</summary>
    public string Username { get; set; } = "";

    /// <summary>For Gmail, an app password. Keep it in a secret / host variable, never in the repo.</summary>
    public string Password { get; set; } = "";

    public string FromAddress { get; set; } = "";

    public string FromName { get; set; } = "FX Intelligence";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed class DispatcherSettings
{
    public bool Enabled { get; set; }

    public int PollSeconds { get; set; } = 10;

    public int BatchSize { get; set; } = 20;

    /// <summary>Attempts before an email is marked failed.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>How long a claimed email stays reserved; a crashed dispatcher's claims are retried after it.</summary>
    public int LeaseSeconds { get; set; } = 300;
}
