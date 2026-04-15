using System.IO;

namespace ShadPS4Launcher.Models;

public sealed class GameEntry
{
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";  // path to eboot.bin or game ID
    public string GameId { get; set; } = "";
    public string? IconPath { get; set; }

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : (Path.Length > 0 ? Path : "Unknown");
}
