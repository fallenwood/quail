using System.Text;

namespace Quail.Services;

public readonly record struct MailAuthenticationCredentials(string Login, string Password);

public static class MailAuthentication
{
    public static bool TryDecodeBase64Token(string encoded, out string value)
    {
        value = string.Empty;

        if (string.IsNullOrWhiteSpace(encoded))
            return false;

        try
        {
            value = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool TryDecodePlainCredentials(string encoded, out MailAuthenticationCredentials credentials)
    {
        credentials = default;

        if (!TryDecodeBase64Token(encoded, out var decoded))
            return false;

        var parts = decoded.Split('\0');
        if (parts.Length < 3)
            return false;

        var login = parts[1];
        var password = parts[2];

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
            return false;

        credentials = new MailAuthenticationCredentials(login, password);
        return true;
    }
}
