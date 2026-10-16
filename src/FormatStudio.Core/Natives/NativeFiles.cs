using FormatStudio.Projects;
using Microsoft.Extensions.FileSystemGlobbing;

namespace FormatStudio.Natives;

/// <summary>
/// The files a native declaration refers to (its plugin, the runtime files of generated code): those of a project
/// folder or of a library package. Paths are relative to the project or package root, with forward slashes.
/// </summary>
public abstract class NativeFiles
{
    /// <summary>The files matching a glob, ordered.</summary>
    public abstract IReadOnlyList<string> Glob(string pattern);

    /// <summary>The content of a file; null when there is none.</summary>
    public abstract byte[]? Read(string path);

    /// <summary>Where a file is, for messages and to recognize a plugin again: a full path or <c>package!/path</c>.</summary>
    public abstract string Describe(string path);

    /// <summary>
    /// <paramref name="path"/> relative to the folder of <paramref name="file"/>, as a path from the root. Throws when it
    /// leads out of the root.
    /// </summary>
    public static string Join(string file, string path)
    {
        var parts = new List<string>();
        var folder = file.Replace('\\', '/');
        var slash = folder.LastIndexOf('/');
        foreach (var part in (slash < 0 ? "" : folder[..slash]).Split('/').Concat(path.Replace('\\', '/').Split('/')))
        {
            switch (part)
            {
                case "" or ".":
                    break;
                case "..":
                    if (parts.Count == 0) throw new Model.FormatDefinitionException($"{path} is outside the project");
                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }

        if (Path.IsPathRooted(path)) throw new Model.FormatDefinitionException($"{path} must be relative");
        return string.Join('/', parts);
    }

    /// <summary>The files of a project folder, read inside its sandbox.</summary>
    public static NativeFiles Folder(string directory, FileSandbox sandbox) => new FolderFiles(directory, sandbox);

    /// <summary>The files of a library package.</summary>
    public static NativeFiles Package(LibraryPackage package) => new PackageFiles(package);

    private sealed class FolderFiles(string directory, FileSandbox sandbox) : NativeFiles
    {
        public override IReadOnlyList<string> Glob(string pattern) =>
            FormatProject.Glob(directory, [pattern], [], sandbox).Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/')).ToList();

        public override byte[]? Read(string path)
        {
            var full = Path.GetFullPath(path, directory);
            return sandbox.Contains(full) && File.Exists(full) ? File.ReadAllBytes(full) : null;
        }

        public override string Describe(string path) => Path.GetFullPath(path, directory);
    }

    private sealed class PackageFiles(LibraryPackage package) : NativeFiles
    {
        public override IReadOnlyList<string> Glob(string pattern)
        {
            var matcher = new Matcher(StringComparison.Ordinal);
            matcher.AddInclude(pattern);
            return matcher.Match(package.Resources.Keys).Files.Select(f => f.Path).Order(StringComparer.Ordinal).ToList();
        }

        public override byte[]? Read(string path) => package.Resources.GetValueOrDefault(path);

        public override string Describe(string path) => $"{package.Origin}!/{path}";
    }
}
