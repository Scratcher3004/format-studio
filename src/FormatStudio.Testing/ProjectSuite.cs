using System.Collections.Concurrent;
using FormatStudio.Codegen;
using FormatStudio.Codegen.Java;
using FormatStudio.Projects;

namespace FormatStudio.Testing;

/// <summary>
/// A project's test cases as unit tests of a test framework: <see cref="Cases"/> has an id per case for the simulator and
/// per case and codegen target (<c>suite/name</c>, <c>suite/name [target]</c>), for a data-driven test such as xUnit's
/// <c>[MemberData]</c>, and <see cref="Run"/> runs the case of an id:
/// <code>
/// static readonly ProjectSuite Suite = ProjectSuite.Get("path/to/project", "csharp", "java-stream");
/// public static IEnumerable&lt;object[]&gt; Cases => Suite.Cases();
///
/// [Theory, MemberData(nameof(Cases))]
/// public void Case(string id)
/// {
///     var result = Suite.Run(id);
///     if (result.Skipped) Assert.Skip(result.Summary);
///     Assert.True(result.Passed, result.Describe());
/// }
/// </code>
/// A target's code is generated and compiled once, when the first of its cases runs, and runs all of its cases then.
/// Without a JDK the cases of Java targets are skipped.
/// </summary>
public sealed class ProjectSuite
{
    private static readonly ConcurrentDictionary<string, ProjectSuite> Loaded = new();

    private readonly Dictionary<string, TestCaseDef> _cases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lazy<IReadOnlyDictionary<string, CaseResult>>> _targets = new(StringComparer.Ordinal);
    private readonly List<string> _ids = [];

    private ProjectSuite(FormatProject project, IReadOnlyList<CodegenTarget> targets, TargetTools tools)
    {
        Project = project;
        Targets = targets;
        foreach (var test in project.Tests) _cases.TryAdd(CaseResult.IdOf(test, null), test);
        _ids.AddRange(_cases.Keys);
        foreach (var target in targets)
        {
            _targets[target.Name] = new Lazy<IReadOnlyDictionary<string, CaseResult>>(() => RunTarget(target, tools));
            _ids.AddRange(project.Tests.Select(t => CaseResult.IdOf(t, target.Name)).Distinct());
        }
    }

    public FormatProject Project { get; }
    /// <summary>The codegen targets the cases run against besides the simulator.</summary>
    public IReadOnlyList<CodegenTarget> Targets { get; }
    /// <summary>The ids of the cases: all of the simulator first, then those of each target.</summary>
    public IReadOnlyList<string> Ids => _ids;

    /// <summary>
    /// The suite of the project at <paramref name="path"/> (its project file or folder) with the given targets (names of
    /// its codegen targets, or of generators for their defaults), loaded once per process.
    /// </summary>
    public static ProjectSuite Get(string path, params string[] targets) =>
        Loaded.GetOrAdd(Path.GetFullPath(path) + "|" + string.Join("|", targets), _ => Load(path, targets, TargetTools.Find()));

    /// <summary>Loads the suite of a project afresh.</summary>
    /// <exception cref="ArgumentException">A target is unknown, or doesn't generate C# or Java.</exception>
    public static ProjectSuite Load(string path, IReadOnlyList<string> targets, TargetTools tools)
    {
        var project = FormatProject.Load(path);
        var resolved = targets.Distinct().Select(name =>
        {
            var target = CodeGeneration.FindTarget(project, name) ?? throw new ArgumentException($"No codegen target or generator '{name}'", nameof(targets));
            return TargetRunner.Supports(project, target) ? target : throw new ArgumentException($"Target '{name}' generates {target.Generator}; tests run against C# and Java targets", nameof(targets));
        }).ToList();
        return new ProjectSuite(project, resolved, tools);
    }

    /// <summary>The ids as rows of one argument, for xUnit's <c>[MemberData]</c>.</summary>
    public IEnumerable<object[]> Cases() => _ids.Select(id => new object[] { id });

    /// <summary>Runs the case of an id from <see cref="Ids"/>.</summary>
    /// <exception cref="GeneratedCodeException">The target's code doesn't generate or compile.</exception>
    public CaseResult Run(string id)
    {
        if (_cases.TryGetValue(id, out var test)) return TestRunner.Run(Project, test);
        foreach (var (name, results) in _targets)
        {
            if (id.EndsWith($" [{name}]", StringComparison.Ordinal) && results.Value.TryGetValue(id, out var result)) return result;
        }

        throw new ArgumentException($"No test case '{id}' in {Project.Settings.Name}", nameof(id));
    }

    private IReadOnlyDictionary<string, CaseResult> RunTarget(CodegenTarget target, TargetTools tools)
    {
        IReadOnlyList<CaseResult> results = Project.Generators.Get(target.Generator) is JavaGenerator && tools.Java == null
            ? Project.Tests.Select(t => new CaseResult(t, [], null) { Skipped = true, SkipReason = JavaToolchain.Missing, Target = target.Name }).ToList()
            : TargetRunner.Run(Project, target, tools);
        var byId = new Dictionary<string, CaseResult>(StringComparer.Ordinal);
        foreach (var result in results) byId.TryAdd(result.Id, result);
        return byId;
    }
}
