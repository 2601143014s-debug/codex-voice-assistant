using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CodexVoiceAssistant.Tests")]

namespace CodexVoiceAssistant.Protocol;

public sealed record CodexAppServerOptions
{
    public string Executable { get; init; } = "codex.exe";
    public string ClientVersion { get; init; } = "2.0.0";
    public string CodexHome { get; init; } = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        "CodexVoiceAssistant",
        "codex-home");
    public string? ModelCatalogPath { get; init; } = ResolveModelCatalog();
    public string WorkingDirectory { get; init; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Codex");
    public string ModelProvider { get; init; } = "DeepSeek";
    public string Model { get; init; } = "deepseek-flash";
    public string Effort { get; init; } = "low";
    public string Sandbox { get; init; } = "danger-full-access";
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan FirstDeltaTimeout { get; init; } = TimeSpan.FromSeconds(45);

    private static string? ResolveModelCatalog()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            ".codex",
            "deepseek-catalog.json");
        return File.Exists(path) ? path : null;
    }
}

public sealed record CodexTurnUpdate(
    string ThreadId,
    string TurnId,
    string Delta,
    bool IsFinal);

public sealed record CodexTurnResult(
    string ThreadId,
    string TurnId,
    string Text);
