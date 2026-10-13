using Microsoft.AspNetCore.Components;

namespace FormatStudio.Web;

/// <summary>Base of the pages and components that work on the project in the URL (<c>/p/{ProjectId}/...</c>).</summary>
public abstract class ProjectComponent : ComponentBase
{
    /// <summary>Bound by the routes of project pages; shared components read the project from the URL as well.</summary>
    [Parameter] public string ProjectId { get; set; } = "";

    [Inject] protected ProjectContext Context { get; set; } = null!;

    /// <summary>The project of the page. The layout only renders project pages whose id is known.</summary>
    protected ProjectService Projects => Context.Current ?? throw new InvalidOperationException($"Unknown project '{Context.Id}'.");

    /// <summary>A link to another page of the same project, e.g. <c>Link("type/Foo")</c>.</summary>
    protected string Link(string page) => Context.Link(page);
}
