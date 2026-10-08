using System;
using System.Diagnostics;

namespace Grid.Auth.Features;

/// <summary>Abstraction for opening the native sign-in in the system browser
/// (testable without process spawning).</summary>
public interface IBrowserLauncher
{
    void Open(string url);
}

/// <summary>Default desktop browser launcher (Windows: UseShellExecute).</summary>
public sealed class ProcessBrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL is required", nameof(url));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}