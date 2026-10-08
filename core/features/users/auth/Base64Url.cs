using System;

namespace Grid.Auth.Features;

/// <summary>RFC 4648 §5 base64url encoding without padding (used across PKCE,
/// token bags and AUTH identifiers).</summary>
public static class Base64Url
{
    public static string Encode(byte[] data)
    {
        if (data is null || data.Length == 0) return string.Empty;
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Decode(string encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return Array.Empty<byte>();
        var standard = encoded.Replace('-', '+').Replace('_', '/');
        switch (standard.Length % 4)
        {
            case 2: standard += "=="; break;
            case 3: standard += "="; break;
        }
        return Convert.FromBase64String(standard);
    }

    /// <summary>True when the string is a non-empty base64url token (alphabet
    /// [A-Za-z0-9_-], no padding).</summary>
    public static bool IsBase64Url(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
        {
            var isLetter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            var isDigit = c >= '0' && c <= '9';
            if (!isLetter && !isDigit && c != '-' && c != '_') return false;
        }
        return true;
    }

    public static bool IsBase64Url(string? value, int minLength, int maxLength)
        => IsBase64Url(value) && value!.Length >= minLength && value.Length <= maxLength;
}