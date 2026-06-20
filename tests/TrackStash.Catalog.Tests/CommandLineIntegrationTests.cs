using System.Diagnostics;
using System.Text.Json;
using TrackStash.Core.Sqlite;

namespace TrackStash.Catalog.Tests;

[Collection("CatalogCli")]
public sealed class CommandLineIntegrationTests
{
    [Fact]
    public async Task Template_Label_TextMode_ReturnsYamlAndExit0()
    {
        var result = await RunCatalogCliAsync("template --kind label");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("apiVersion: catalog.trackstash/v1", result.StdOut);
        Assert.Contains("kind: Label", result.StdOut);
    }

    [Fact]
    public async Task ValidateEntity_WithValidTemplate_JsonMode_ReturnsExit0()
    {
        var root = Path.GetDirectoryName(GetCatalogProjectPath())!;
        var filePath = Path.Combine(root, "..", "..", "templates", "entities", "label.v1.yaml");
        filePath = Path.GetFullPath(filePath);

        var result = await RunCatalogCliAsync($"validate-entity --file \"{filePath}\" --output json");

        Assert.Equal(0, result.ExitCode);

        using var doc = JsonDocument.Parse(result.StdOut);
        var rootJson = doc.RootElement;
        Assert.True(GetProperty(rootJson, "ok").GetBoolean());
        Assert.True(GetProperty(GetProperty(rootJson, "data"), "isValid").GetBoolean());
    }

    [Fact]
    public async Task ValidateEntity_WithInvalidYaml_JsonMode_ReturnsExit1()
    {
        var yamlPath = Path.Combine(Path.GetTempPath(), $"catalog-invalid-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(yamlPath, "kind: Label\nspec: [unterminated");

            var result = await RunCatalogCliAsync($"validate-entity --file \"{yamlPath}\" --output json");

            Assert.Equal(1, result.ExitCode);

            using var doc = JsonDocument.Parse(result.StdOut);
            var rootJson = doc.RootElement;
            Assert.False(GetProperty(rootJson, "ok").GetBoolean());
            Assert.False(GetProperty(GetProperty(rootJson, "data"), "isValid").GetBoolean());
        }
        finally
        {
            DeleteIfExists(yamlPath);
        }
    }

    [Fact]
    public async Task Doctor_WithInitializedDb_JsonMode_ReturnsOkAndExit0()
    {
        var dbPath = TempDb();
        try
        {
            var provider = new SqliteStorageProvider(dbPath);
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var result = await RunCatalogCliAsync($"doctor --db-path \"{dbPath}\" --output json");

            Assert.Equal(0, result.ExitCode);

            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;

            Assert.True(GetProperty(root, "ok").GetBoolean());
            Assert.Equal(0, GetProperty(root, "exitCode").GetInt32());
            Assert.Equal("doctor", GetProperty(root, "command").GetString());

            var data = GetProperty(root, "data");
            Assert.Equal(dbPath, GetProperty(data, "databasePath").GetString());
            Assert.True(GetProperty(data, "databaseReachable").GetBoolean());
            Assert.Equal(1, GetProperty(data, "currentMigrationVersion").GetInt32());

            var warnings = GetProperty(data, "warnings").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Contains("Catalog is empty.", warnings);
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task Doctor_WithUninitializedDb_JsonMode_ReturnsExit1WithFindings()
    {
        var dbPath = TempDb();
        try
        {
            // Intentionally leave DB uninitialized to exercise diagnostics path.
            var result = await RunCatalogCliAsync($"doctor --db-path \"{dbPath}\" --output json");

            Assert.Equal(1, result.ExitCode);

            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            Assert.False(GetProperty(root, "ok").GetBoolean());
            Assert.Equal(1, GetProperty(root, "exitCode").GetInt32());

            var findings = GetProperty(GetProperty(root, "data"), "findings")
                .EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .ToArray();

            Assert.Contains(findings, f => f.Contains("Run bootstrap init-db/migrate", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task RepairIndexes_DryRun_JsonMode_ReturnsExit0AndDryRunPayload()
    {
        var dbPath = TempDb();
        try
        {
            var provider = new SqliteStorageProvider(dbPath);
            await provider.Migrations.ApplyPendingMigrationsAsync();

            var result = await RunCatalogCliAsync($"repair-indexes --db-path \"{dbPath}\" --dry-run --output json");

            Assert.Equal(0, result.ExitCode);

            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            Assert.True(GetProperty(root, "ok").GetBoolean());
            Assert.Equal(0, GetProperty(root, "exitCode").GetInt32());

            var data = GetProperty(root, "data");
            Assert.True(GetProperty(data, "dryRun").GetBoolean());
            Assert.False(GetProperty(data, "performed").GetBoolean());
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCatalogCliAsync(string arguments)
    {
        var projectPath = GetCatalogProjectPath();

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build -v q --project \"{projectPath}\" -- {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        var stdOutTask = process!.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return (process.ExitCode, stdOut, stdErr);
    }

    private static JsonElement GetProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var direct))
            return direct;

        var pascal = char.ToUpperInvariant(propertyName[0]) + propertyName[1..];
        if (element.TryGetProperty(pascal, out var pascalCase))
            return pascalCase;

        throw new KeyNotFoundException($"Property '{propertyName}' not found.");
    }

    private static string GetCatalogProjectPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TrackStash.Catalog", "TrackStash.Catalog.csproj");
            if (File.Exists(candidate))
                return candidate;

            // Running from test bin: project root is expected at .../trackstash-catalog
            if (string.Equals(dir.Name, "trackstash-catalog", StringComparison.OrdinalIgnoreCase))
            {
                candidate = Path.Combine(dir.FullName, "src", "TrackStash.Catalog", "TrackStash.Catalog.csproj");
                if (File.Exists(candidate))
                    return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate TrackStash.Catalog.csproj from test base directory.");
    }

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"catalog-cli-{Guid.NewGuid():N}.db");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

[CollectionDefinition("CatalogCli", DisableParallelization = true)]
public sealed class CatalogCliCollectionDefinition
{
}
