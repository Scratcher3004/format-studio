using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FormatStudio.Projects;

namespace FormatStudio.Web;

/// <summary>Command line settings of the web UI.</summary>
public sealed record StudioOptions
{
    /// <summary>Opened at startup; <c>/</c> then goes straight to it (<c>--project</c>).</summary>
    public string? Project { get; init; }
    /// <summary>
    /// A folder whose <c>*.fstudio.yaml</c> files are listed on the start page (<c>--workspace</c>). When set, the web UI
    /// doesn't read, write or list anything outside it.
    /// </summary>
    public string? Workspace { get; init; }
    /// <summary>Where the list of recent projects is kept (<c>--state-dir</c>).</summary>
    public string StateDirectory { get; init; } = DefaultStateDirectory;

    /// <summary><c>~/.config/format-studio</c> on Linux, <c>%APPDATA%\format-studio</c> on Windows.</summary>
    public static string DefaultStateDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "format-studio");
}

/// <summary>A project the start page can open.</summary>
/// <param name="Id">Used in URLs (<c>/p/{id}/...</c>); derived from the path, so links stay valid across restarts.</param>
/// <param name="File">Full path of the <c>*.fstudio.yaml</c>.</param>
/// <param name="Opened">When it was last opened in the web UI, if it is in the recent list.</param>
/// <param name="InWorkspace">Found by the workspace scan.</param>
public sealed record ProjectEntry(string Id, string File, DateTimeOffset? Opened, bool InWorkspace)
{
    public string Name => Path.GetFileName(File)[..^ProjectSettings.Extension.Length];
}

/// <summary>
/// The projects known to the web UI (recent, workspace, <c>--project</c>) and the loaded ones. Shared by all browser
/// connections, so everyone viewing a project sees the same <see cref="ProjectService"/> and its changes.
/// </summary>
public sealed class ProjectStore
{
    private const int MaxRecent = 20;
    private static readonly HashSet<string> SkippedFolders = ["bin", "obj", "node_modules", "generated"];

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ProjectService> _loaded = new(StringComparer.Ordinal);
    private readonly string _recentFile;
    private List<RecentProject> _recent;
    private List<string> _workspace = [];

    public ProjectStore(StudioOptions options)
    {
        Options = options;
        Sandbox = FileSandbox.For(options.Workspace);
        _recentFile = Path.Combine(options.StateDirectory, "recent.json");
        _recent = ReadRecent();
        ScanWorkspace();
        if (options.Project != null) Initial = Open(options.Project);
    }

    public StudioOptions Options { get; }
    /// <summary>The workspace, if any, as the boundary for every file the web UI touches.</summary>
    public FileSandbox Sandbox { get; }
    /// <summary>The project given with <c>--project</c>.</summary>
    public ProjectEntry? Initial { get; }
    /// <summary>Raised when the list of known projects changes.</summary>
    public event Action? Changed;

    /// <summary>Recently opened projects first (newest first), then the rest of the workspace by path.</summary>
    public IReadOnlyList<ProjectEntry> Known()
    {
        lock (_lock)
        {
            var workspace = _workspace.ToHashSet(PathComparer);
            // Projects opened in earlier runs without this workspace stay in the file but aren't offered.
            var entries = _recent.Where(r => Sandbox.Contains(r.File)).Select(r => new ProjectEntry(IdOf(r.File), r.File, r.Opened, workspace.Contains(r.File))).ToList();
            var recent = _recent.Select(r => r.File).ToHashSet(PathComparer);
            entries.AddRange(_workspace.Where(f => !recent.Contains(f)).Select(f => new ProjectEntry(IdOf(f), f, null, true)));
            return entries;
        }
    }

    public ProjectEntry? Find(string id) => Known().FirstOrDefault(e => e.Id == id);

    /// <summary>The loaded project with this id (loaded on first use), or null if the id is unknown.</summary>
    public ProjectService? Get(string id)
    {
        lock (_lock)
        {
            if (_loaded.TryGetValue(id, out var service)) return service;
        }

        var entry = Find(id);
        if (entry == null) return null;
        lock (_lock)
        {
            if (!_loaded.TryGetValue(id, out var service)) _loaded[id] = service = new ProjectService(entry.File, Sandbox);
            return service;
        }
    }

    /// <summary>Adds a project file, or the single one in a folder, to the recent list.</summary>
    public ProjectEntry Open(string path)
    {
        var file = FormatProject.ResolveFile(path, Sandbox);
        lock (_lock)
        {
            _recent.RemoveAll(r => PathComparer.Equals(r.File, file));
            _recent.Insert(0, new RecentProject(file, DateTimeOffset.Now));
            if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
            WriteRecent();
        }

        Changed?.Invoke();
        return Find(IdOf(file))!;
    }

    /// <summary>Creates a project from <see cref="ProjectTemplate"/> and opens it.</summary>
    public ProjectEntry Create(string directory, string? name, string endian, string length) =>
        Open(ProjectTemplate.Create(Sandbox.Check(directory), name, endian, length));

    /// <summary>Removes a project from the recent list (the files stay).</summary>
    public void Forget(string id)
    {
        lock (_lock)
        {
            _recent.RemoveAll(r => IdOf(r.File) == id);
            WriteRecent();
        }

        Changed?.Invoke();
    }

    /// <summary>Looks for <c>*.fstudio.yaml</c> files in the workspace again.</summary>
    public void ScanWorkspace()
    {
        var found = new List<string>();
        if (Sandbox.Root is { } workspace && Directory.Exists(workspace)) Scan(new DirectoryInfo(workspace), 0, found);
        found.Sort(StringComparer.Ordinal);
        lock (_lock) _workspace = found;
        Changed?.Invoke();
    }

    private static void Scan(DirectoryInfo directory, int depth, List<string> found)
    {
        try
        {
            found.AddRange(directory.EnumerateFiles("*" + ProjectSettings.Extension).Select(f => f.FullName));
            if (depth >= 8) return;
            foreach (var child in directory.EnumerateDirectories())
            {
                if (child.Name.StartsWith('.') || SkippedFolders.Contains(child.Name) || child.LinkTarget != null) continue;
                Scan(child, depth + 1, found);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable folders are skipped.
        }
    }

    /// <summary>The file name without extension and a hash of the full path, e.g. <c>bedrock-3fa2c1d0</c>.</summary>
    public static string IdOf(string file)
    {
        var name = Path.GetFileName(file);
        if (name.EndsWith(ProjectSettings.Extension, StringComparison.OrdinalIgnoreCase)) name = name[..^ProjectSettings.Extension.Length];
        var slug = new StringBuilder();
        foreach (var c in name.ToLowerInvariant()) slug.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        var key = OperatingSystem.IsWindows() ? file.ToUpperInvariant() : file;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8];
        return $"{slug.ToString().Trim('-')}-{hash}";
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record RecentProject(string File, DateTimeOffset Opened);

    private List<RecentProject> ReadRecent()
    {
        try
        {
            if (!File.Exists(_recentFile)) return [];
            var recent = JsonSerializer.Deserialize<List<RecentProject>>(File.ReadAllText(_recentFile)) ?? [];
            // Entries outside the workspace are kept without looking at them (see Known).
            return recent.Where(r => !Sandbox.Contains(r.File) || File.Exists(r.File)).Take(MaxRecent).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void WriteRecent()
    {
        // The recent list is a convenience; failing to save it must not fail opening a project.
        try
        {
            Directory.CreateDirectory(Options.StateDirectory);
            File.WriteAllText(_recentFile, JsonSerializer.Serialize(_recent, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
