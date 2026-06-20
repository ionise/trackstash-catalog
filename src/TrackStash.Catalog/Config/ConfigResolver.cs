using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TrackStash.Catalog.Config;

public static class ConfigResolver
{
    public static CatalogConfig Resolve(IReadOnlyDictionary<string, string?> cliOptions)
    {
        var config = new CatalogConfig();

        var configFilePath = GetValue(cliOptions, "config")
            ?? Environment.GetEnvironmentVariable("TRACKSTASH_CONFIG");

        if (!string.IsNullOrWhiteSpace(configFilePath))
            ApplyFile(config, configFilePath);

        ApplyEnvironmentVariables(config);
        ApplyCliOptions(config, cliOptions);

        return config;
    }

    private static void ApplyFile(CatalogConfig config, string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Config file not found: {filePath}");

        var yaml = File.ReadAllText(filePath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var file = deserializer.Deserialize<CatalogConfig>(yaml);
        if (file is null)
            return;

        if (!string.IsNullOrWhiteSpace(file.Provider))
            config.Provider = file.Provider;

        if (!string.IsNullOrWhiteSpace(file.Catalog))
            config.Catalog = file.Catalog;

        if (!string.IsNullOrWhiteSpace(file.Sqlite?.DbPath))
            config.Sqlite.DbPath = file.Sqlite.DbPath;

        MergeCatalogMappings(config, file);

        if (!string.IsNullOrWhiteSpace(file.Output?.Format))
            config.Output.Format = file.Output.Format;

        if (!string.IsNullOrWhiteSpace(file.Logging?.Verbosity))
            config.Logging.Verbosity = file.Logging.Verbosity;
    }

    private static void ApplyEnvironmentVariables(CatalogConfig config)
    {
        var catalog = Environment.GetEnvironmentVariable("TRACKSTASH_CATALOG");
        if (!string.IsNullOrWhiteSpace(catalog)) config.Catalog = catalog;

        var provider = Environment.GetEnvironmentVariable("TRACKSTASH_PROVIDER");
        if (!string.IsNullOrWhiteSpace(provider)) config.Provider = provider;

        var dbPath = Environment.GetEnvironmentVariable("TRACKSTASH_SQLITE_DB_PATH");
        if (!string.IsNullOrWhiteSpace(dbPath)) config.Sqlite.DbPath = dbPath;

        var outputFormat = Environment.GetEnvironmentVariable("TRACKSTASH_OUTPUT_FORMAT");
        if (!string.IsNullOrWhiteSpace(outputFormat)) config.Output.Format = outputFormat;

        var verbosity = Environment.GetEnvironmentVariable("TRACKSTASH_VERBOSITY");
        if (!string.IsNullOrWhiteSpace(verbosity)) config.Logging.Verbosity = verbosity;

        ApplyCatalogEnvironmentVariables(config);
    }

    private static void ApplyCliOptions(CatalogConfig config, IReadOnlyDictionary<string, string?> options)
    {
        if (GetValue(options, "catalog") is { } catalog)
            config.Catalog = catalog;

        if (GetValue(options, "provider") is { } provider)
        {
            config.Provider = provider;
            EnsureCatalogTarget(config, config.Catalog).Provider = provider;
        }

        if (GetValue(options, "db-path") is { } dbPath)
        {
            config.Sqlite.DbPath = dbPath;
            EnsureCatalogTarget(config, config.Catalog).Sqlite.DbPath = dbPath;
        }

        if (GetValue(options, "output") is { } format)
            config.Output.Format = format;

        if (GetValue(options, "verbosity") is { } verbosity)
            config.Logging.Verbosity = verbosity;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var value) ? value : null;

    private static void MergeCatalogMappings(CatalogConfig config, CatalogConfig file)
    {
        if (file.Catalogs is null || file.Catalogs.Count == 0)
            return;

        foreach (var (name, target) in file.Catalogs)
        {
            if (string.IsNullOrWhiteSpace(name) || target is null)
                continue;

            var merged = EnsureCatalogTarget(config, name);
            if (!string.IsNullOrWhiteSpace(target.Provider))
                merged.Provider = target.Provider;

            if (!string.IsNullOrWhiteSpace(target.Sqlite?.DbPath))
                merged.Sqlite.DbPath = target.Sqlite.DbPath;
        }
    }

    private static void ApplyCatalogEnvironmentVariables(CatalogConfig config)
    {
        var vars = Environment.GetEnvironmentVariables();
        foreach (var keyObj in vars.Keys)
        {
            var key = keyObj?.ToString();
            if (string.IsNullOrWhiteSpace(key))
                continue;

            const string providerSuffix = "_PROVIDER";
            const string sqliteDbPathSuffix = "_SQLITE_DB_PATH";
            const string prefix = "TRACKSTASH_CATALOG_";

            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(key, "TRACKSTASH_CATALOG", StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = key[prefix.Length..];
            if (string.IsNullOrWhiteSpace(remainder))
                continue;

            string? catalogName = null;
            if (remainder.EndsWith(providerSuffix, StringComparison.OrdinalIgnoreCase))
            {
                catalogName = remainder[..^providerSuffix.Length];
                var value = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(catalogName))
                    continue;

                EnsureCatalogTarget(config, NormalizeCatalogName(catalogName)).Provider = value;
                continue;
            }

            if (remainder.EndsWith(sqliteDbPathSuffix, StringComparison.OrdinalIgnoreCase))
            {
                catalogName = remainder[..^sqliteDbPathSuffix.Length];
                var value = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(catalogName))
                    continue;

                EnsureCatalogTarget(config, NormalizeCatalogName(catalogName)).Sqlite.DbPath = value;
            }
        }
    }

    private static CatalogTargetConfig EnsureCatalogTarget(CatalogConfig config, string? catalogName)
    {
        var key = NormalizeCatalogName(catalogName);
        if (!config.Catalogs.TryGetValue(key, out var target) || target is null)
        {
            target = new CatalogTargetConfig();
            config.Catalogs[key] = target;
        }

        return target;
    }

    private static string NormalizeCatalogName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "default";

        return name.Trim().ToLowerInvariant().Replace('_', '-');
    }
}
