namespace Fjord.Features;

/// <summary>The one place features are switched on. Order here = order in the UI.</summary>
public static class FeatureRegistry
{
    public static IReadOnlyList<IAppFeature> All { get; } =
    [
        // Session toolbar
        new OpenSftpFeature(),
        new TunnelsFeature(),
        new InstallKeyFeature(),
        new ColorPromptFeature(),
        new LogFeature(),
        new SnippetsFeature(),
        new DuplicateSessionFeature(),

        // Sidebar tools
        new KeyGeneratorFeature(),
        new KeyGuideFeature(),
        new TunnelGuideFeature(),
        new SnippetsManagerFeature(),
        new ImportPuttyFeature(),
        new SettingsFeature(),
    ];

    public static IEnumerable<IAppFeature> SessionFeatures => All.Where(f => f.Scope == FeatureScope.Session);
    public static IEnumerable<IAppFeature> GlobalFeatures => All.Where(f => f.Scope == FeatureScope.Global);
}
