using FormatStudio.Dsl;
using FormatStudio.Model;

namespace FormatStudio.Projects;

/// <summary>Creates new projects (used by <c>format-studio new</c> and the web UI).</summary>
public static class ProjectTemplate
{
    /// <summary>
    /// Writes <c>&lt;name&gt;.fstudio.yaml</c> and the empty <c>types</c>, <c>packets</c> and <c>samples</c> folders into
    /// <paramref name="directory"/> (created if missing) and returns the project file. A <see cref="ProjectKind.Library"/>
    /// gets a <paramref name="version"/> (default 0.1.0), the defaults listed in <paramref name="inherit"/> come from the
    /// projects using it, and it has no <c>packets</c> folder.
    /// </summary>
    public static string Create(string directory, string? name = null, string endian = "little", string length = "varuint32",
        ProjectKind kind = ProjectKind.Project, string? version = null, IReadOnlyCollection<string>? inherit = null)
    {
        directory = Path.GetFullPath(directory);
        name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : name.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new FormatDefinitionException($"'{name}' can't be used as a project name");
        var library = kind == ProjectKind.Library;
        if (library && !TypeExpressions.IsIdentifier(name))
            throw new FormatDefinitionException($"'{name}' can't be used as a library name: it must be an identifier ([A-Za-z_][A-Za-z0-9_]*), as its definitions are referenced as {name}.Name");
        if (!library && inherit is { Count: > 0 }) throw new FormatDefinitionException("Only libraries inherit defaults");
        version = string.IsNullOrWhiteSpace(version) ? "0.1.0" : version.Trim();
        if (version.IndexOfAny(['"', '\\', '\n', '\r']) >= 0) throw new FormatDefinitionException($"'{version}' can't be used as a version");
        var file = Path.Combine(directory, name + ProjectSettings.Extension);
        if (File.Exists(file)) throw new FormatDefinitionException($"{file} already exists");
        if (Directory.Exists(directory) && Directory.GetFiles(directory, "*" + ProjectSettings.Extension).Length > 0)
            throw new FormatDefinitionException($"{directory} already contains a project");

        var yaml = library
            ? $"""
              name: {name}
              kind: library
              version: "{version}"
              defaults:
                endian: {endian}
                length: {length}
                string: utf8{(inherit is { Count: > 0 } ? $"\n  inherit: [{string.Join(", ", inherit)}]" : "")}
              sources:
                - types/**/*.yaml
              samples:
                - samples/**/*.yaml
              """
            : $"""
              name: {name}
              defaults:
                endian: {endian}
                length: {length}
                string: utf8
              sources:
                - types/**/*.yaml
                - packets/**/*.yaml
              samples:
                - samples/**/*.yaml
              """;
        ProjectSettings.Parse(yaml, file);
        Directory.CreateDirectory(Path.Combine(directory, "types"));
        if (!library) Directory.CreateDirectory(Path.Combine(directory, "packets"));
        Directory.CreateDirectory(Path.Combine(directory, "samples"));
        File.WriteAllText(file, yaml + "\n");
        return file;
    }
}
