# How to install (Grid native client)

> Canonical, must-match-top-level copy is the root `INSTALL.md`. This file adds
> exact patch blocks. Do both files drift apart. Read the whole guide once
> before editing anything.

## 0. Copy

Do **not** copy `ui/` into the verification build. Every cluster below lands at
its MDBO Hive canonical path (see root `MANIFEST.md`).

| Source (this payload) | Destination (Grid repo) |
| --- | --- |
| `core/features/users/auth/*.cs` (runtime, includes `ui/`) | `grid/core/features/users/auth/` |
| `core/api/Auth/*.cs` | `grid/core/api/Auth/` |
| `core/app/GridAuthHost.cs` | `grid/core/app/GridAuthHost.cs` |
| `config/grid.auth.config.example.json` → `config/grid.auth.config.json` | `grid/config/` |
| `config/grid.auth.server-config.example.json` + `config/grid.auth.env.example` | `grid/deploy/auth/` (real AUTH deployment, not runtime) |
| `docs/reference/*` | `grid/docs/reference/` |
| `docs/how-to/*` | `grid/docs/how-to/` |
| `tools/auth/scripts/*` | `grid/tools/auth/scripts/` |
| `tests/Grid.Auth.Tests/*` | `grid/tests/Grid.Auth.Tests/` |

Copy **verbatim**. Do not edit namespaces; do not merge partial files.

## 1. csproj (typically 0–1 small edits)

The Grid WinUI project is SDK-style (WinUI 3, Microsoft.WindowsAppSDK). Enabled
`ImplicitUsings`/nullable are NOT required — every payload file carries explicit
`using` directives.

If your project uses `<Compile Remove>` or a manual `<Compile>` list, add:

```xml
<ItemGroup>
  <Compile Include="core\features\users\auth\**\*.cs" Exclude="core\features\users\auth\ui\**" />
  <Compile Include="core\api\Auth\**\*.cs" />
  <Compile Include="core\app\GridAuthHost.cs" />
  <Compile Include="tools\auth\scripts\**" />
</ItemGroup>
```

Better: delete the manual list and rely on default globbing. **No NuGet package
additions are required** (System.Text.Json, System.Net.Http, System.Net.Sockets
are in-box on net9.0; WinUI is already a project dependency).

The UI files (`core/features/users/auth/ui/*.cs`) compile only where
`Microsoft.WindowsAppSDK` is present — which is the Grid WinUI project. The
WiX/MSIX project in `Grid.sln` must build after the feature is present; its
project includes no C# compilation.

## 2. Compose at startup — `App.xaml.cs`

```csharp
private Grid.Auth.Features.ShellAuthController? _auth;
internal Grid.Auth.Features.ShellAuthController Auth => _auth!;

private void OnLaunched(LaunchActivatedEventArgs args)
{
    var options = Grid.Auth.AuthOptions.FromJson(
        System.IO.File.ReadAllText("config/grid.auth.config.json"));
    var service = Grid.Auth.GridAuthHost.Compose(options);
    _auth = new Grid.Auth.Features.ShellAuthController(service, OnAuthStateChanged);
    // ...existing window/shell creation...
}
```

## 3. Wire the shell — the only real editing work

A `MainWindow`/`Shell` page binds **once** to `_auth`:

```csharp
public sealed partial class Shell : Page
{
    private readonly ShellAuthController _auth;

    private void OnAuthStateChanged(AuthSnapshot snapshot, ImageSource? avatar)
    {
        // Signed out: hide product panels + console + discovery:
        ProductPanels.Visibility = snapshot.IsSignedIn ? Visible : Collapsed;
        ConsolePane.Visibility      = snapshot.IsSignedIn ? Visible : Collapsed;
        SignInPanel.Visibility      = snapshot.IsSignedIn ? Collapsed : Visible;

        if (snapshot.IsSignedIn)
        {
            AccountName.Text  = snapshot.User!.Name;
            AccountEmail.Text = snapshot.User!.Email;
            ProviderBadge.Text = snapshot.User.AuthProvider;
            Avatar.Visibility = Visible;
            if (avatar != null) Avatar.Source = avatar; // circular via Ellipse/PersonPicture
        }
        else { Avatar.Visibility = Collapsed; }
        ErrorText.Text = snapshot.Error ?? "";
    }

    private void OnConsentRequired() // surfaced via state — see below
}
```

Buttons call the controller:

```csharp
MicrosoftBtn.Click += (_, _) => _auth.SignInWith("microsoft", "select_account");
GitHubBtn.Click   += (_, _) => _auth.SignInWith("github");
GoogleBtn.Click   += (_, _) => _auth.SignInWith("google");
EmailBtn.Click    += async (_, _) =>
{
    await _auth.RequestMagicAsync(EmailBox.Text, "login");
    _ = WaitForMagicAsync();
};
LogoutBtn.Click   += (_, _) => _auth.SignOut();
```

Wait for magic + consent:

```csharp
private async Task WaitForMagicAsync()
{
    var token = await _auth.WaitMagicAsync();
    if (token == null) { ErrorText.Text = "Magic link expired; request a new one."; return; }
    await _auth.ConsumeTokenAsync(token);
    // If snapshot.Phase == ConsentRequired: show gate.Docs, then:
    // await _auth.AcceptConsentAsync(accepted);
}
```

## 4. Config

- Copy `config/grid.auth.config.example.json` → `config/grid.auth.config.json` in
  the Grid repo (adjust `baseUrl`). Set `Content` CopyToOutput if the app reads
  it from output; otherwise resolve next to `AppContext.BaseDirectory`.

## 5. Backend (deployment, also in payload `config/`)

- Deploy `grid.auth.server-config.example.json` + `grid.auth.env.example` into the
  AUTH deployment: `allowedOrigins` must contain `http://127.0.0.1:51706`,
  cookie-name envs must match the desktop config, `publicClients` must
  include `grid-windows` loopback config. Provider secrets live only server-side
  (no desktop change required).

## 6. Optional deep link `grid://auth`

For the browser→app return (macOS/Windows native agent), register in the app
manifest:

```xml
<Extensions>
  <uap:Extension Category="windows.protocol">
    <uap:Protocol Name="grid" />
  </uap:Extension>
</Extensions>
```

`CoreApplicationView.Activated` → if `args.Kind == Protocol`, pass
`args.Uri.ToString()` to `_auth.Service` flow hop. The loopback path does not
need this — it is the default path.

## 7. Verification

- `tools/auth/scripts/verify.ps1` on the built package (or the C# runner copied
  in `tests/Grid.Auth.Tests/`, see `docs/how-to/setup-and-verify.md`).