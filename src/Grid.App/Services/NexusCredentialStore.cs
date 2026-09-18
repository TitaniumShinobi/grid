using Windows.Security.Credentials;

namespace Grid.App.Services;

public interface INexusCredentialStore
{
    bool IsConfigured { get; }
    string? LoadApiKey();
    bool SaveApiKey(string apiKey);
    bool Remove();
}

public sealed class NexusCredentialStore : INexusCredentialStore
{
    private const string Resource = "Grid.NexusMods";
    private const string UserName = "api-key";

    public bool IsConfigured => LoadApiKey() is not null;

    public string? LoadApiKey()
    {
        try
        {
            var credential = new PasswordVault().Retrieve(Resource, UserName);
            credential.RetrievePassword();
            return IsValidApiKey(credential.Password) ? credential.Password : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    public bool SaveApiKey(string apiKey)
    {
        if (!IsValidApiKey(apiKey)) return false;
        try
        {
            var vault = new PasswordVault();
            try
            {
                var existing = vault.Retrieve(Resource, UserName);
                vault.Remove(existing);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { }
            vault.Add(new PasswordCredential(Resource, UserName, apiKey.Trim()));
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    public bool Remove()
    {
        try
        {
            var vault = new PasswordVault();
            var existing = vault.Retrieve(Resource, UserName);
            vault.Remove(existing);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return !IsConfigured;
        }
    }

    private static bool IsValidApiKey(string? value) =>
        value is { Length: >= 20 and <= 256 } && value.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
}
