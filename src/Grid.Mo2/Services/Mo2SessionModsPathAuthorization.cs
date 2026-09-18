using System.Collections.Concurrent;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2SessionModsPathAuthorization(IMo2PathCanonicalizer paths)
    : IMo2ModsPathAuthorization
{
    private readonly ConcurrentDictionary<InstallationReferenceId, string> roots = [];

    public Mo2ModsRootAuthorization AuthorizeModsRoot(
        InstallationReferenceId referenceId,
        string modsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modsRoot);
        if (!paths.TryCanonicalize(modsRoot, out var canonical, out var error))
        {
            throw new ArgumentException(error ?? "The mods root could not be canonicalized.", nameof(modsRoot));
        }

        roots[referenceId] = canonical;
        return new(referenceId, canonical);
    }

    public bool IsModsRootAuthorized(InstallationReferenceId referenceId, string modsRoot) =>
        roots.TryGetValue(referenceId, out var authorized) && paths.Equals(authorized, modsRoot);

    public void Revoke(InstallationReferenceId referenceId) => roots.TryRemove(referenceId, out _);
}
