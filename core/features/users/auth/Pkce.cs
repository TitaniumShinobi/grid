using System;
using System.Security.Cryptography;
using System.Text;

namespace Grid.Auth.Features;

/// <summary>
/// RFC 7636 PKCE plus opaque state generation and validation. Matches the
/// AUTH authorize contract: state 32–512 chars, verifier 43–128 chars,
/// S256 challenge of exactly 43 chars.
/// </summary>
public static class Pkce
{
    public const int StateMinLength = 32;
    public const int StateMaxLength = 512;
    public const int VerifierMinLength = 43;
    public const int VerifierMaxLength = 128;
    public const int ChallengeLength = 43;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    public static string GenerateState() => RandomString(44);
    public static string GenerateVerifier() => RandomString(96);

    public static string ComputeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url.Encode(hash);
    }

    public static bool IsValidState(string? value) => IsValidRange(value, StateMinLength, StateMaxLength);
    public static bool IsValidVerifier(string? value) => IsValidRange(value, VerifierMinLength, VerifierMaxLength);

    public static bool IsValidChallenge(string? value)
        => value is not null && value.Length == ChallengeLength && Base64Url.IsBase64Url(value);

    private static bool IsValidRange(string? value, int min, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length < min || value.Length > max) return false;
        foreach (var c in value)
        {
            var isLetter = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            var isDigit = c >= '0' && c <= '9';
            if (!isLetter && !isDigit && c != '-' && c != '.' && c != '_' && c != '~') return false;
        }
        return true;
    }

    private static string RandomString(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }
        return new string(chars);
    }
}