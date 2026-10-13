namespace FormatStudio.Tests;

internal static class TestPaths
{
    /// <summary>The repository root (the folder with FormatStudio.slnx), found from the test assembly's location.</summary>
    public static string Root { get; } = FindRoot();

    public static string JavaSample => Path.Combine(Root, "samples", "java-protocol");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FormatStudio.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Repository root not found");
    }

    /// <summary>A new empty folder under the system temp folder.</summary>
    public static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "format-studio-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
