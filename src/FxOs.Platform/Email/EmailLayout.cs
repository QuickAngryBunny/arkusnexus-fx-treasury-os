using System.Net;
using System.Text;

namespace FxOs.Platform.Email;

/// <summary>
/// The Violeta email layout from the prototype (O-01, T-04, T-09): a violet header with an eyebrow, title and subtitle,
/// then a greeting, paragraphs, one button and a small footnote. Table-based with inline styles so mail clients render
/// it. Every value passed in is plain text and HTML-encoded here; only <see cref="Strong"/> marks parts as bold.
/// </summary>
internal sealed class EmailLayout
{
    private const string Violet = "#6C10E0";
    private const string Plum = "#3E1F55";
    private const string Ink = "#24152F";
    private const string Muted = "#6B6378";
    private const string Lilac = "#EBDDFB";
    private const string Font = "Manrope, 'Segoe UI', Arial, sans-serif";

    private readonly List<string> _blocks = [];
    private readonly List<string> _text = [];

    public required string Eyebrow { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public required string Greeting { get; init; }
    public string? Footnote { get; set; }

    /// <summary>A paragraph; parts made with <see cref="Strong"/> are bold.</summary>
    public EmailLayout Paragraph(params Part[] parts)
    {
        _blocks.Add($"<p style=\"margin:0 0 14px;font-size:15px;line-height:1.6;color:{Ink}\">{Html(parts)}</p>");
        _text.Add(Text(parts));
        return this;
    }

    /// <summary>A quoted note, e.g. the inviter's message (T-04).</summary>
    public EmailLayout Quote(string quote, string by)
    {
        _blocks.Add(
            $"<p style=\"margin:0 0 14px;padding:12px 16px;border-left:3px solid #B06BEA;background:#F6F3FB;font-size:15px;line-height:1.6;color:#4A3D57\">“{Enc(quote)}” — {Enc(by)}</p>");
        _text.Add($"“{quote}” — {by}");
        return this;
    }

    public EmailLayout Bullets(params string[] items)
    {
        var lis = string.Concat(items.Select(i => $"<li style=\"margin:0 0 4px\">{Enc(i)}</li>"));
        _blocks.Add($"<ul style=\"margin:0 0 14px;padding-left:20px;font-size:15px;line-height:1.6;color:#4A3D57\">{lis}</ul>");
        _text.AddRange(items.Select(i => "- " + i));
        return this;
    }

    public EmailLayout Button(string label, Uri link)
    {
        if (link.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("Email links must be http(s).", nameof(link));
        }

        var href = Enc(link.AbsoluteUri);
        _blocks.Add(
            $"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:6px 0 18px\"><tr><td style=\"border-radius:16px;background:{Violet}\">" +
            $"<a href=\"{href}\" style=\"display:inline-block;padding:15px 22px;font-family:{Font};font-size:15px;font-weight:800;color:#FFFFFF;text-decoration:none;border-radius:16px\">{Enc(label)}</a>" +
            "</td></tr></table>");
        _text.Add($"{label}: {link.AbsoluteUri}");
        return this;
    }

    public (string Html, string Text) Render()
    {
        var subtitle = Subtitle is null
            ? ""
            : $"<div style=\"font-size:15px;color:{Lilac};margin-top:8px\">{Enc(Subtitle)}</div>";
        var footnote = Footnote is null
            ? ""
            : $"<p style=\"margin:4px 0 0;font-size:12px;line-height:1.5;color:{Muted}\">{Enc(Footnote)}</p>";

        var html = new StringBuilder()
            .Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"></head>")
            .Append($"<body style=\"margin:0;padding:0;background:#E9E6EE;font-family:{Font};color:{Ink}\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#E9E6EE;padding:32px 12px\"><tr><td align=\"center\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;background:#FFFFFF;border-radius:24px;overflow:hidden\">")
            .Append($"<tr><td style=\"background:{Violet};padding:32px 28px;color:#FFFFFF\">")
            .Append($"<div style=\"font-size:14px;color:{Lilac}\">{Enc(Eyebrow)}</div>")
            .Append($"<div style=\"font-size:30px;font-weight:800;line-height:1.15;margin-top:8px\">{Enc(Title)}</div>")
            .Append(subtitle)
            .Append("</td></tr>")
            .Append("<tr><td style=\"padding:24px 28px\">")
            .Append($"<p style=\"margin:0 0 14px;font-size:15px;line-height:1.6;color:{Ink}\">{Enc(Greeting)}</p>")
            .Append(string.Concat(_blocks))
            .Append(footnote)
            .Append("</td></tr>")
            .Append($"<tr><td style=\"padding:16px 28px;border-top:1px solid #EFE6FB;font-size:12px;color:{Muted}\">FX Intelligence · <span style=\"color:{Plum}\">Treasury OS</span></td></tr>")
            .Append("</table></td></tr></table></body></html>")
            .ToString();

        var text = new StringBuilder()
            .AppendLine(Title)
            .AppendLine()
            .AppendLine(Greeting)
            .AppendLine();
        foreach (var block in _text)
        {
            text.AppendLine(block).AppendLine();
        }

        if (Footnote is not null)
        {
            text.AppendLine(Footnote);
        }

        return (html, text.ToString().TrimEnd() + "\n");
    }

    public static Part Plain(string text) => new(text, false);

    public static Part Strong(string text) => new(text, true);

    private static string Html(Part[] parts) =>
        string.Concat(parts.Select(p => p.Bold ? $"<strong>{Enc(p.Text)}</strong>" : Enc(p.Text)));

    private static string Text(Part[] parts) => string.Concat(parts.Select(p => p.Text));

    private static string Enc(string value) => WebUtility.HtmlEncode(value);

    public readonly record struct Part(string Text, bool Bold);
}
