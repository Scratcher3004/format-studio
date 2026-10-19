using System.Diagnostics;
using FormatStudio.Codegen;

namespace FormatStudio.Testing;

/// <summary>A JDK to compile and run generated Java with.</summary>
public sealed class JavaToolchain(string javac, string java)
{
    public string Javac { get; } = javac;
    public string Java { get; } = java;

    /// <summary>What to do when <see cref="Find"/> finds no JDK.</summary>
    public const string Missing = "No JDK: set JAVA_HOME or FORMAT_STUDIO_JDK, or put javac on the PATH";

    /// <summary>
    /// The JDK in <paramref name="home"/>, <c>FORMAT_STUDIO_JDK</c>, <c>JAVA_HOME</c> or on the PATH, or null when
    /// there is none.
    /// </summary>
    public static JavaToolchain? Find(string? home = null)
    {
        var executable = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var candidate in new[] { home, Environment.GetEnvironmentVariable("FORMAT_STUDIO_JDK"), Environment.GetEnvironmentVariable("JAVA_HOME") })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var javac = Path.Combine(candidate, "bin", "javac" + executable);
            if (File.Exists(javac)) return new JavaToolchain(javac, Path.Combine(candidate, "bin", "java" + executable));
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var javac = Path.Combine(folder, "javac" + executable);
            if (File.Exists(javac)) return new JavaToolchain(javac, Path.Combine(folder, "java" + executable));
        }

        return null;
    }

    /// <summary>
    /// Writes the generated files and extra sources (default package) to <paramref name="directory"/> and compiles them
    /// for Java 8 with all lint warnings as errors. Returns the folder with the classes.
    /// </summary>
    public string Compile(GeneratedOutput output, string extraSource, string directory)
    {
        var sources = Path.Combine(directory, "src");
        var classes = Path.Combine(directory, "classes");
        Directory.CreateDirectory(classes);
        var files = new List<string>();
        foreach (var file in output.Files.Append(new GeneratedFile("Harness.java", extraSource)))
        {
            var path = Path.Combine(sources, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file.Content);
            files.Add(path);
        }

        var list = Path.Combine(directory, "sources.txt");
        File.WriteAllLines(list, files.Select(f => "\"" + f.Replace("\\", "/") + "\""));
        var (exit, stdout, stderr) = Execute(Javac, ["-encoding", "UTF-8", "--release", "8", "-Xlint:all,-options,-cast", "-Werror", "-d", classes, "@" + list], null);
        if (exit != 0) throw new GeneratedCodeException(string.Join("\n", (stdout + stderr).Split('\n').Take(60)));
        return classes;
    }

    /// <summary>Runs a main class and returns its output.</summary>
    public string Run(string classPath, string mainClass, string input)
    {
        var (exit, stdout, stderr) = Execute(Java, ["-cp", classPath, mainClass], input);
        if (exit != 0) throw new GeneratedCodeException($"{mainClass} failed with exit code {exit}: {stderr}");
        return stdout.Replace("\r\n", "\n");
    }

    private static (int Exit, string Stdout, string Stderr) Execute(string program, IEnumerable<string> arguments, string? input)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new GeneratedCodeException($"Can't start {program}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input != null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
