namespace NSDeck.Core.Services;

public static class AppDataMigration
{
    private const string CurrentFolderName = "NSDeck";
    private const string LegacyFolderName = "DomainDnsManager";
    public const string MigrationMarkerName = ".migrated-from-domain-dns-manager";

    public static string PrepareApplicationRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var currentRoot = Path.Combine(localAppData, CurrentFolderName);
        var legacyRoot = Path.Combine(localAppData, LegacyFolderName);
        MigrateMissingData(legacyRoot, currentRoot);
        return currentRoot;
    }

    public static void MigrateMissingData(string legacyRoot, string currentRoot)
    {
        Directory.CreateDirectory(currentRoot);
        var marker = Path.Combine(currentRoot, MigrationMarkerName);
        if (!Directory.Exists(legacyRoot) || File.Exists(marker)) return;

        CopyMissingFiles(legacyRoot, currentRoot);
        File.WriteAllText(marker,
            $"NSDeck copied existing settings, snapshots, and logs from {LegacyFolderName} on {DateTimeOffset.Now:O}." + Environment.NewLine);
    }

    private static void CopyMissingFiles(string sourceRoot, string destinationRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            if (source.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(sourceRoot, source);
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(source, destination);
        }
    }
}
