using System.Collections.Concurrent;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2SessionPathAuthorization(IMo2PathCanonicalizer paths) : IMo2SessionPathAuthorization
{
    private readonly ConcurrentDictionary<InstallationReferenceId, string> profilesRoots = [];

    public Mo2ProfilesRootAuthorization AuthorizeProfilesRoot(
        InstallationReferenceId referenceId,
        string profilesRoot)
    {
        if (!paths.TryCanonicalize(profilesRoot, out var canonical, out var error))
        {
            throw new ArgumentException($"The profiles-root authorization is invalid: {error}", nameof(profilesRoot));
        }

        profilesRoots[referenceId] = canonical;
        return new(referenceId, canonical);
    }

    public bool IsProfilesRootAuthorized(InstallationReferenceId referenceId, string profilesRoot) =>
        paths.TryCanonicalize(profilesRoot, out var canonical, out _) &&
        profilesRoots.TryGetValue(referenceId, out var authorized) &&
        paths.Equals(canonical, authorized);

    public void Revoke(InstallationReferenceId referenceId) => profilesRoots.TryRemove(referenceId, out _);
}
