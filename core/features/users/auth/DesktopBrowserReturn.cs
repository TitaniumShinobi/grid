using System;
using System.Net;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace Life.Auth.Desktop;

/// <summary>
/// Per-request browser return. No OAuth credentials or workspace state live here.
/// Complete only after the host has validated/persisted its session, or has failed.
/// An activation nonce can raise the existing window only; it never grants a session.
/// </summary>
public sealed class DesktopBrowserReturn
{
    private readonly string _appName;
    private readonly string _activationNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly Func<Task<bool>> _activateExisting;
    private readonly object _gate = new();
    private readonly DateTimeOffset _expiresAt;
    private string _state = "pending";

    public DesktopBrowserReturn(string appName, Func<Task<bool>> activateExisting, DateTimeOffset now)
    {
        _appName = appName;
        _activateExisting = activateExisting ?? throw new ArgumentNullException(nameof(activateExisting));
        _expiresAt = now.AddMinutes(10);
    }

    public void Complete(bool authenticated)
    {
        lock (_gate) { if (_state == "pending") _state = authenticated ? "authenticated" : "failed"; }
    }

    public void Invalidate() { lock (_gate) _state = "expired"; }

    // Host must check method POST, exact loopback path, request-size bounds and
    // same-origin policy before calling. Never route this to authentication logic.
    public async Task<bool> TryActivateAsync(string nonce, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now >= _expiresAt || (_state != "authenticated" && _state != "failed")) return false;
            if (!FixedEquals(_activationNonce, nonce)) return false;

        }
        return await _activateExisting();
    }

    public string Status(string nonce, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!FixedEquals(_activationNonce, nonce) || now >= _expiresAt) return "expired";
            return _state;
        }
    }

    private static bool FixedEquals(string expected, string actual)
    {
        if (actual is null || actual.Length != expected.Length) return false;
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(expected), System.Text.Encoding.ASCII.GetBytes(actual));
    }

    public string Html()
    {
        // nonce is generated hex, not callback input. No dynamic script interpolation.
        var name = WebUtility.HtmlEncode(_appName);
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>Return to " + name + "</title></head>" +
            "<body><p id=\"status\">Completing sign-in…</p><form method=\"post\" action=\"/desktop/activate\">" +
            "<input type=\"hidden\" name=\"activation\" value=\"" + _activationNonce + "\">" +
            "<button id=\"open\" disabled>Open " + name + "</button></form>" +
            "<script src=\"/desktop/return.js\"></script></body></html>";
    }

    public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    public const string Script = "'use strict';history.replaceState(null,'',location.pathname);" +
        "const form=document.querySelector('form'),button=document.getElementById('open'),label=document.getElementById('status');" +
        "const activation=form.elements.activation.value;let attempts=0;" +
        "async function openApp(){try{const r=await fetch('/desktop/activate',{method:'POST',body:new URLSearchParams({activation}),redirect:'error',credentials:'omit'});" +
        "if(!r.ok)label.textContent='Use Open to return to the app.';}catch{label.textContent='Return to the app to retry.';}}" +
        "form.addEventListener('submit',e=>{e.preventDefault();openApp();});" +
        "async function poll(){try{const r=await fetch('/desktop/status',{method:'POST',body:new URLSearchParams({activation}),redirect:'error',credentials:'omit'});" +
        "if(!r.ok)throw Error();const state=await r.text();" +
        "if(state==='authenticated'||state==='failed'){label.textContent=state==='authenticated'?'Sign-in complete.':'Sign-in could not complete. Retry in the app.';button.disabled=false;await openApp();return;}" +
        "if(state==='expired')throw Error();if(++attempts>=120)throw Error();setTimeout(poll,500);" +
        "}catch{label.textContent='Sign-in interrupted. Return to the app and retry.';}}poll();";
}
