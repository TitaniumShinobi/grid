using System;
using System.Text.Json;

namespace Grid.Auth.Api;

/// <summary>
/// Structured AUTH failure surfaced to Grid UI copy: HTTP <see cref="Status"/>
/// plus the AUTH error <see cref="Code"/> (e.g. ENROLLMENT_REQUIRED,
/// NO_SESSION, invalid_grant, MAGIC_EMAIL_NOT_CONFIGURED).
/// </summary>
public sealed class AuthApiException : Exception
{
    public AuthApiException(int status, string code, string? description = null)
        : base(description ?? code)
    {
        this.Status = status;
        this.Code = code;
        this.Description = description;
    }

    public int Status { get; }
    public string Code { get; }
    public string? Description { get; }

    public static string CodeFromBody(System.Text.Json.JsonDocument body, string fallback, int status)
    {
        if (body.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var name in new[] { "code", "error" })
            {
                if (body.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = v.GetString();
                    if (!string.IsNullOrEmpty(s)) return s!;
                }
            }
        }
        return fallback ?? $"HTTP_{status}";
    }
}