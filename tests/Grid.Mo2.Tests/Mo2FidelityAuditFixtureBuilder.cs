using Grid.Core.Models;

static class Mo2FidelityAuditFixtureBuilder
{
    public static FidelityAuditContext Context(string executableFingerprint = "exe-fingerprint") => new(
        new("game.skyrim-special-edition"), new("installation.mo2.audit"), new("profile.mo2.audit"),
        new("reference.mo2.audit"), "catalog-1", "connection-1", "profile-snapshot-1", "inventory-1",
        new("resolved.mo2.audit"), "environment-1", new("outputs.mo2.audit"), "outputs-1",
        new("executable.mo2.audit"), executableFingerprint);
}
