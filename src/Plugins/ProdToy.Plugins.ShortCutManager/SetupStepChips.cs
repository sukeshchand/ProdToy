namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Click-to-insert snippets for the edit form's "Setup steps" box. The set
/// is shell-specific: cmd uses <c>set VAR=value</c>, PowerShell uses
/// <c>$env:VAR = "value"</c>. Each snippet is inserted on its own line, since
/// the launcher treats one line as one statement.
/// </summary>
static class SetupStepChips
{
    public static string[] For(LaunchShell shell) => shell == LaunchShell.PowerShell
        ? new[]
        {
            "$env:NODE_ENV = \"development\"",
            "$env:NEXT_LOG_LEVEL = \"error\"",
            "$env:ASPNETCORE_ENVIRONMENT = \"Development\"",
            "$env:DOTNET_ENVIRONMENT = \"Development\"",
            "$env:Serilog__MinimumLevel__Default = \"Information\"",
            "$env:VAR = \"value\"",
        }
        : new[]
        {
            "set NODE_ENV=development",
            "set NEXT_LOG_LEVEL=info",
            "set ASPNETCORE_ENVIRONMENT=Development",
            "set DOTNET_ENVIRONMENT=Development",
            "set Serilog__MinimumLevel__Default=Information",
            "set VAR=value",
        };
}
