using System.Text.Json;
using System.Text.Json.Serialization;
using TradingClient.Application.Risk;

namespace TradingClient.Persistence;

/// <summary>
/// 风控限额配置的 JSON 文件存储：文件不存在返回 null，由调用方回落内置默认配置。
/// SQLite/PostgreSQL 持久化落地前先用文件，实现 IRiskLimitsStore 即可替换。
/// </summary>
public sealed class JsonRiskLimitsStore(string filePath) : IRiskLimitsStore
{
    public async Task<RiskLimitsProfile?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(filePath))
            return null;

        await using var stream = File.OpenRead(filePath);
        return await JsonSerializer.DeserializeAsync(
            stream, PersistenceJsonContext.Default.RiskLimitsProfile, ct);
    }

    public async Task SaveAsync(RiskLimitsProfile profile, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(
            stream, profile, PersistenceJsonContext.Default.RiskLimitsProfile, ct);
    }
}

[JsonSerializable(typeof(RiskLimitsProfile))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext;
