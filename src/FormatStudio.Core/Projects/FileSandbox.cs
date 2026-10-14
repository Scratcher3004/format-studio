using FormatStudio.Model;

namespace FormatStudio.Projects;

/// <summary>
/// Keeps file access inside one folder (the web UI's <c>--workspace</c>). A path is inside if it is below the root both
/// as written and after following every symbolic link or junction on the way, so links can't lead out of the folder.
/// <see cref="Unrestricted"/> allows everything (the command line, the web UI without a workspace).
/// </summary>
public sealed class FileSandbox
{
    private const int MaxLinks = 40;
    private readonly string? _realRoot;

    private FileSandbox(string? root)
    {
        if (root == null) return;
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _realRoot = RealPath(Root);
    }

    public static FileSandbox Unrestricted { get; } = new(null);

    /// <summary>A sandbox for <paramref name="root"/>, or <see cref="Unrestricted"/> for null.</summary>
    public static FileSandbox For(string? root) => root == null ? Unrestricted : new FileSandbox(root);

    /// <summary>The folder that can't be left, or null if unrestricted.</summary>
    public string? Root { get; }
    public bool IsRestricted => Root != null;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whether the path (which need not exist yet) is inside the root.</summary>
    public bool Contains(string path)
    {
        if (Root == null) return true;
        var full = Path.GetFullPath(path);
        // The lexical check comes first, so paths outside are rejected without touching the file system there.
        return IsBelow(full, Root) && IsBelow(RealPath(full), _realRoot!);
    }

    /// <summary>The full path, or an exception if it is outside the root.</summary>
    public string Check(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Contains(full)) throw new FormatDefinitionException($"{full} is outside the workspace {Root}");
        return full;
    }

    /// <summary>
    /// The files in a folder (and its subfolders if <paramref name="recursive"/>). When restricted, linked folders are not
    /// entered and linked files are only listed if they point inside the root.
    /// </summary>
    public IEnumerable<string> EnumerateFiles(string directory, string pattern = "*", bool recursive = false)
    {
        if (Root == null)
            return Directory.EnumerateFiles(directory, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
        return Walk(new DirectoryInfo(Check(directory)), pattern, recursive);
    }

    /// <summary>The subfolders of a folder; when restricted, only those inside the root (links are followed to check).</summary>
    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        if (Root == null) return Directory.EnumerateDirectories(directory);
        return new DirectoryInfo(Check(directory)).EnumerateDirectories()
            .Where(d => d.LinkTarget == null || Contains(d.FullName)).Select(d => d.FullName);
    }

    private IEnumerable<string> Walk(DirectoryInfo directory, string pattern, bool recursive)
    {
        foreach (var file in directory.EnumerateFiles(pattern))
        {
            if (file.LinkTarget == null || Contains(file.FullName)) yield return file.FullName;
        }

        if (!recursive) yield break;
        foreach (var child in directory.EnumerateDirectories())
        {
            // Linked folders are skipped even when they point inside: they could form cycles.
            if (child.LinkTarget != null) continue;
            foreach (var file in Walk(child, pattern, recursive)) yield return file;
        }
    }

    private static bool IsBelow(string path, string root) =>
        path.Equals(root, PathComparison)
        || (path.StartsWith(root, PathComparison) && path.Length > root.Length
            && (Path.EndsInDirectorySeparator(root) || path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar));

    /// <summary>The path with every link on the way replaced by its target; the part that doesn't exist yet is kept as is.</summary>
    private static string RealPath(string path)
    {
        var links = 0;
        return RealPath(Path.GetFullPath(path), ref links);
    }

    private static string RealPath(string path, ref int links)
    {
        var root = Path.GetPathRoot(path)!;
        var parts = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            // FileInfo also reads links to folders and broken links (which would create their target when written to).
            if (new FileInfo(next).LinkTarget is { } target)
            {
                if (++links > MaxLinks) throw new FormatDefinitionException($"Too many links in {path}");
                next = RealPath(Path.GetFullPath(target, current), ref links);
            }
            else if (!Path.Exists(next))
            {
                return Path.Combine([next, .. parts[(i + 1)..]]);
            }

            current = next;
        }

        return current;
    }
}
