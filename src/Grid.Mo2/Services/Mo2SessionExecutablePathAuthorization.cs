using System.Collections.Concurrent;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2SessionExecutablePathAuthorization : IMo2ExecutablePathAuthorization
{
    private readonly IMo2PathCanonicalizer paths;
    private readonly ConcurrentDictionary<InstallationReferenceId, ConcurrentDictionary<string, byte>> authorized = [];

    public Mo2SessionExecutablePathAuthorization(IMo2PathCanonicalizer paths) =>
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public Mo2ExecutablePathAuthorization AuthorizePath(
        InstallationReferenceId referenceId,
        string exactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exactPath);
        if (!paths.TryNormalizeLexically(exactPath, out var lexical, out var lexicalError))
        {
            throw new ArgumentException(lexicalError ?? "The executable path could not be normalized.", nameof(exactPath));
        }

        if (!paths.TryCanonicalize(exactPath, out var canonical, out var error))
        {
            throw new ArgumentException(error ?? "The executable path could not be canonicalized.", nameof(exactPath));
        }

        var pathsForReference = authorized
            .GetOrAdd(referenceId, static _ => new(StringComparer.OrdinalIgnoreCase));
        pathsForReference[lexical] = 0;
        pathsForReference[canonical] = 0;
        return new(referenceId, canonical);
    }

    public bool IsPathAuthorized(InstallationReferenceId referenceId, string exactPath)
    {
        // The negative path deliberately stays lexical so an untrusted, not-yet-authorized
        // location is not touched merely to answer an authorization question.
        if (!paths.TryNormalizeLexically(exactPath, out var lexical, out _))
        {
            return false;
        }

        return authorized.TryGetValue(referenceId, out var pathsForReference) &&
            pathsForReference.ContainsKey(lexical);
    }

    public void Revoke(InstallationReferenceId referenceId, string? exactPath = null)
    {
        if (exactPath is null)
        {
            authorized.TryRemove(referenceId, out _);
            return;
        }

        if (paths.TryNormalizeLexically(exactPath, out var lexical, out _) &&
            authorized.TryGetValue(referenceId, out var pathsForReference))
        {
            pathsForReference.TryRemove(lexical, out _);
            if (paths.TryCanonicalize(exactPath, out var canonical, out _))
            {
                pathsForReference.TryRemove(canonical, out _);
            }
            if (pathsForReference.IsEmpty)
            {
                authorized.TryRemove(referenceId, out _);
            }
        }
    }
}
