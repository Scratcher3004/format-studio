using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace FormatStudio.Web;

/// <summary>
/// The project of the current page, taken from the URL (<c>/p/{id}/...</c>). One per browser connection; read from
/// the URL on every access, so it is already right while the router renders the next page.
/// </summary>
public sealed class ProjectContext : IDisposable
{
    public const string Prefix = "p/";

    private readonly NavigationManager _navigation;
    private ProjectService? _subscribed;

    public ProjectContext(ProjectStore store, NavigationManager navigation)
    {
        Store = store;
        _navigation = navigation;
        _navigation.LocationChanged += OnLocationChanged;
    }

    public ProjectStore Store { get; }

    /// <summary>The id in the URL, or null outside of a project.</summary>
    public string? Id => IdFrom(_navigation.ToBaseRelativePath(_navigation.Uri));

    /// <summary>The project in the URL, or null outside of a project or for an unknown id.</summary>
    public ProjectService? Current => Id is { } id ? Store.Get(id) : null;

    /// <summary>Raised when the page switches to another project, and when the current project is reloaded.</summary>
    public event Action? Changed;

    /// <summary>A link to a page of the current project, e.g. <c>Link("type/Foo")</c> gives <c>p/{id}/type/Foo</c>.</summary>
    public string Link(string page) => LinkTo(Id ?? throw new InvalidOperationException("Not inside a project."), page);

    public static string LinkTo(string id, string page = "") => Prefix + Uri.EscapeDataString(id) + (page.Length > 0 ? "/" + page : "");

    public static string? IdFrom(string relativePath)
    {
        if (!relativePath.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var rest = relativePath[Prefix.Length..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        var id = Uri.UnescapeDataString(end < 0 ? rest : rest[..end]);
        return id.Length > 0 ? id : null;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e) => Follow();

    /// <summary>Follows reloads of the project in the URL; called on every navigation and once by the layout.</summary>
    public void Follow()
    {
        var current = Current;
        if (current == _subscribed) return;
        if (_subscribed != null) _subscribed.Changed -= OnProjectChanged;
        _subscribed = current;
        if (current != null) current.Changed += OnProjectChanged;
        Changed?.Invoke();
    }

    private void OnProjectChanged() => Changed?.Invoke();

    public void Dispose()
    {
        _navigation.LocationChanged -= OnLocationChanged;
        if (_subscribed != null) _subscribed.Changed -= OnProjectChanged;
    }
}
