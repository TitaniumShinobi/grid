using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2SessionContentPathAuthorization : IMo2SessionContentPathAuthorization
{
    private readonly IMo2PathCanonicalizer paths;
    private readonly object sync = new();
    private readonly Dictionary<(InstallationReferenceId ReferenceId, Mo2ContentRootKind Kind), string> roots = [];

    public Mo2SessionContentPathAuthorization(IMo2PathCanonicalizer paths) =>
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public Mo2ContentRootAuthorization AuthorizeRoot(
        InstallationReferenceId referenceId,
        Mo2ContentRootKind kind,
        string root)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!paths.TryCanonicalize(root, out var canonical, out var error))
        {
            throw new ArgumentException($"The content root cannot be authorized: {error}", nameof(root));
        }

        lock (sync)
        {
            roots[(referenceId, kind)] = canonical;
        }

        return new(referenceId, kind, canonical);
    }

    public bool IsRootAuthorized(
        InstallationReferenceId referenceId,
        Mo2ContentRootKind kind,
        string root)
    {
        if (!paths.TryCanonicalize(root, out var canonical, out _))
        {
            return false;
        }

        lock (sync)
        {
            return roots.TryGetValue((referenceId, kind), out var authorized) &&
                string.Equals(authorized, canonical, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Revoke(InstallationReferenceId referenceId, Mo2ContentRootKind? kind = null)
    {
        lock (sync)
        {
            if (kind is { } selected)
            {
                roots.Remove((referenceId, selected));
                return;
            }

            foreach (var key in roots.Keys.Where(key => key.ReferenceId == referenceId).ToArray())
            {
                roots.Remove(key);
            }
        }
    }
}
