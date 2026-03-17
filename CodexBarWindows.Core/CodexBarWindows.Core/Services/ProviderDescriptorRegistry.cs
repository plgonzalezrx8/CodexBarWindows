namespace CodexBarWindows.Services;

public sealed record ProviderDescriptor(string Id, string Name, bool EnabledByDefault);

public static class ProviderDescriptorRegistry
{
    public static IReadOnlyList<ProviderDescriptor> All { get; } =
    [
        new("codex", "Codex", true),
        new("claude", "Claude", true),
        new("cursor", "Cursor", true),
        new("gemini", "Gemini", false),
        new("antigravity", "Antigravity", false),
        new("copilot", "Copilot", false),
        new("openrouter", "OpenRouter", false),
        new("kiro", "Kiro", false),
        new("jetbrains", "JetBrains AI", false),
        new("augment", "Augment", false),
    ];
}
