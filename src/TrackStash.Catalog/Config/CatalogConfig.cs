namespace TrackStash.Catalog.Config;

public sealed class CatalogConfig
{
    public string Catalog { get; set; } = "default";

    public string Provider { get; set; } = "sqlite";

    public SqliteConfig Sqlite { get; set; } = new();

    public Dictionary<string, CatalogTargetConfig> Catalogs { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public OutputConfig Output { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();
}

public sealed class SqliteConfig
{
    public string? DbPath { get; set; }
}

public sealed class CatalogTargetConfig
{
    public string? Provider { get; set; }

    public SqliteConfig Sqlite { get; set; } = new();
}

public sealed class OutputConfig
{
    public string Format { get; set; } = "text";
}

public sealed class LoggingConfig
{
    public string Verbosity { get; set; } = "normal";
}
