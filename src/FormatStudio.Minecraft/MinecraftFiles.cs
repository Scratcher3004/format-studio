namespace FormatStudio.Minecraft;

/// <summary>The runtime files of the library's native types, embedded in the assembly (<c>Files/</c>).</summary>
internal static class MinecraftFiles
{
    public static string Text(string name)
    {
        using var stream = typeof(MinecraftFiles).Assembly.GetManifestResourceStream($"Files/{name}")
            ?? throw new InvalidOperationException($"The file {name} is missing from the assembly");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
