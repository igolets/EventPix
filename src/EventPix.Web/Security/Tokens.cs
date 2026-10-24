using System.Buffers.Text;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using EventPix.Web.Configuration;
using EventPix.Web.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace EventPix.Web.Security;

internal static class Tokens
{
    /// <summary>192 bits of CSPRNG output, base64url (32 chars). Unguessable by design.</summary>
    public static string NewUrlToken()
    {
        return Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
    }

    public static string Hash(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public static string NewOtp()
    {
        return RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
    }

    /// <summary>Returns lower-cased address or null when invalid.</summary>
    public static string? NormalizeEmail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 254)
        {
            return null;
        }

        var s = raw.Trim().ToLowerInvariant();
        return MailAddress.TryCreate(s, out var a) && a.Address == s ? s : null;
    }
}

/// <summary>Server-side secret for HMACs (OTP hashes, unsubscribe signatures). Lives in DataPath.</summary>
internal sealed class SecretPepper
{
    private readonly byte[] _key;

    public SecretPepper(IOptions<EventPixOptions> options)
    {
        var dir = Path.GetFullPath(options.Value.DataPath);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "pepper.key");
        if (File.Exists(file))
        {
            _key = Convert.FromBase64String(File.ReadAllText(file).Trim());
        }
        else
        {
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(file, Convert.ToBase64String(_key));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    public string Hmac(string data)
    {
        return Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(data)));
    }

    public bool Verify(string data, string signature)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hmac(data)), Encoding.UTF8.GetBytes(signature));
    }
}

internal sealed class EventTokenService(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("EventPix.EventToken.v1");

    public (string Token, string Hash, string Protected) Create()
    {
        var token = Tokens.NewUrlToken();
        return (token, Tokens.Hash(token), _protector.Protect(token));
    }

    public string Reveal(Event ev)
    {
        return _protector.Unprotect(ev.TokenProtected);
    }

    public string Url(Event ev, string publicBaseUrl)
    {
        return $"{publicBaseUrl.TrimEnd('/')}/e/{Reveal(ev)}";
    }
}
