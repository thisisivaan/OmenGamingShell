namespace OmenGamingShell;

public sealed class TaskWindowEntry
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string? ExecutablePath { get; init; }
}
