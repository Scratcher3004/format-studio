using FormatStudio.Model;
using FormatStudio.Projects;
using FormatStudio.Web;

namespace FormatStudio.Tests;

/// <summary>Creating projects and the web UI's list of projects.</summary>
public class ProjectStoreTests
{
    [Fact]
    public void TemplateCreatesALoadableProject()
    {
        var directory = Path.Combine(TestPaths.NewTempDirectory(), "my-format");
        var file = ProjectTemplate.Create(directory, endian: "big", length: "u16");

        Assert.Equal(Path.Combine(directory, "my-format.fstudio.yaml"), file);
        var project = FormatProject.Load(directory);
        Assert.Equal("my-format", project.Settings.Name);
        Assert.Equal(Endian.Big, project.Settings.Defaults.Endian);
        Assert.True(Directory.Exists(Path.Combine(directory, "types")));
        Assert.Throws<FormatDefinitionException>(() => ProjectTemplate.Create(directory, "other"));
    }

    [Fact]
    public void TemplateRejectsInvalidSettings()
    {
        var directory = TestPaths.NewTempDirectory();
        Assert.ThrowsAny<Exception>(() => ProjectTemplate.Create(directory, "x", endian: "middle"));
        Assert.Empty(Directory.GetFileSystemEntries(directory));
    }

    [Fact]
    public void OpenedProjectsAreRememberedNewestFirst()
    {
        var root = TestPaths.NewTempDirectory();
        var options = new StudioOptions { StateDirectory = Path.Combine(root, "state") };
        var first = ProjectTemplate.Create(Path.Combine(root, "a"));
        var second = ProjectTemplate.Create(Path.Combine(root, "b"));

        var store = new ProjectStore(options);
        store.Open(Path.Combine(root, "a"));
        var opened = store.Open(second);

        var again = new ProjectStore(options);
        Assert.Equal([second, first], again.Known().Select(e => e.File));
        Assert.Equal(opened.Id, again.Known()[0].Id);
        Assert.Equal("b", again.Get(opened.Id)!.Project!.Settings.Name);

        again.Forget(opened.Id);
        Assert.Equal([first], new ProjectStore(options).Known().Select(e => e.File));
    }

    [Fact]
    public void WorkspaceProjectsAreListedWithoutOpening()
    {
        var root = TestPaths.NewTempDirectory();
        var file = ProjectTemplate.Create(Path.Combine(root, "nested", "proto"));
        ProjectTemplate.Create(Path.Combine(root, "bin", "ignored"));

        var store = new ProjectStore(new StudioOptions { Workspace = root, StateDirectory = Path.Combine(root, ".state") });

        var entry = Assert.Single(store.Known());
        Assert.Equal(file, entry.File);
        Assert.True(entry.InWorkspace);
        Assert.Null(entry.Opened);
        Assert.NotNull(store.Get(entry.Id)?.Project);
        Assert.Null(store.Get("unknown-00000000"));
    }

    [Fact]
    public void InitialProjectIsOpened()
    {
        var root = TestPaths.NewTempDirectory();
        var file = ProjectTemplate.Create(Path.Combine(root, "p"));
        var store = new ProjectStore(new StudioOptions { Project = Path.Combine(root, "p"), StateDirectory = Path.Combine(root, "state") });
        Assert.Equal(file, store.Initial?.File);
        Assert.Throws<FileNotFoundException>(() => new ProjectStore(new StudioOptions { Project = Path.Combine(root, "missing"), StateDirectory = root }));
    }

    [Fact]
    public void IdsAreStableAndReadFromUrls()
    {
        var id = ProjectStore.IdOf("/work/My Proto.fstudio.yaml");
        Assert.Equal(id, ProjectStore.IdOf("/work/My Proto.fstudio.yaml"));
        Assert.NotEqual(id, ProjectStore.IdOf("/other/My Proto.fstudio.yaml"));
        Assert.Matches("^my-proto-[0-9a-f]{8}$", id);

        Assert.Equal(id, ProjectContext.IdFrom(ProjectContext.LinkTo(id, "type/Foo")));
        Assert.Equal(id, ProjectContext.IdFrom($"p/{id}?x=1"));
        Assert.Null(ProjectContext.IdFrom("projects"));
        Assert.Null(ProjectContext.IdFrom("p/"));
    }
}
