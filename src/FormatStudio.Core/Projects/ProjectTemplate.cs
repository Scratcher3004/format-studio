using FormatStudio.Model;

namespace FormatStudio.Projects;

/// <summary>Creates new projects (used by <c>format-studio new</c> and the web UI).</summary>
public static class ProjectTemplate
{
    /// <summary>
    /// Writes <c>&lt;name&gt;.fstudio.yaml</c> and the empty <c>types</c>, <c>packets</c> and <c>samples</c> folders into
    /// <paramref name="directory"/> (created if missing) and returns the project file.
    /// </summary>
    public static string Create(string directory, string? name = null, string endian = "little", string length = "varuint32")
    {
        directory = Path.GetFullPath(directory);
        name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : name.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new FormatDefinitionException($"'{name}' can't be used as a project name");
        var file = Path.Combine(directory, name + ProjectSettings.Extension);
        if (File.Exists(file)) throw new FormatDefinitionException($"{file} already exists");
        if (Directory.Exists(directory) && Directory.GetFiles(directory, "*" + ProjectSettings.Extension).Length > 0)
            throw new FormatDefinitionException($"{directory} already contains a project");

        var yaml = $"""
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
        Directory.CreateDirectory(Path.Combine(directory, "packets"));
        Directory.CreateDirectory(Path.Combine(directory, "samples"));
        File.WriteAllText(file, yaml + "\n");
        return file;
    }
}
