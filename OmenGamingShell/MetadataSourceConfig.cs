namespace OmenGamingShell;

public sealed class MetadataSourceConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? BaseUrl { get; set; }
    public string? ClientId { get; set; }
    public bool IsEnabled { get; set; } = true;

    public override string ToString() => Name;
}

public sealed class MetadataSettings
{
    public string PrimarySourceId { get; set; } = string.Empty;
    public List<MetadataSourceConfig> Sources { get; set; } = [];
}
