using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Grid.Auth.Features;

/// <summary>Abstraction over the at-rest token bag store. Production Grid uses
/// <see cref="DpapiTokenStore"/> (DPAPI, CurrentUser scope). Tests use
/// <see cref="InMemoryTokenStore"/>.</summary>
public interface ITokenStore
{
    Task<TokenBag?> LoadAsync(string key, CancellationToken ct = default);
    Task SaveAsync(string key, TokenBag bag, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Non-persistent store for tests and dev-only runs.</summary>
public sealed class InMemoryTokenStore : ITokenStore
{
    private readonly Dictionary<string, TokenBag> _bags = new(StringComparer.Ordinal);

    public Task<TokenBag?> LoadAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(this._bags.TryGetValue(key, out var bag) ? bag : null);
    }

    public Task SaveAsync(string key, TokenBag bag, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        this._bags[key] = bag;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        this._bags.Remove(key);
        return Task.CompletedTask;
    }
}