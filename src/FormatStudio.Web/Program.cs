using FormatStudio.Importers;
using FormatStudio.Importers.Mojang;
using FormatStudio.Web;
using FormatStudio.Web.Components;
using MudBlazor.Services;

ImporterRegistry.Register(new MojangImport());

// The content root is the build output, not the current folder (which is the caller's, see RunWorkingDirectory).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

// All optional: --project <file|folder> (or FORMATSTUDIO_PROJECT) opens a project at "/", --workspace <folder> lists the
// projects in a folder on the start page, --state-dir <folder> moves the list of recent projects.
var options = new StudioOptions
{
    Project = builder.Configuration["project"] ?? Environment.GetEnvironmentVariable("FORMATSTUDIO_PROJECT"),
    Workspace = builder.Configuration["workspace"] is { } workspace ? Path.GetFullPath(workspace) : null,
    StateDirectory = builder.Configuration["state-dir"] ?? StudioOptions.DefaultStateDirectory,
};

// A local tool started with "dotnet run": serve MudBlazor's assets from the build output in every environment.
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ProjectStore>();
builder.Services.AddScoped<ProjectContext>();

var app = builder.Build();
// Fails at startup (instead of on the first request) if --project can't be opened.
app.Services.GetRequiredService<ProjectStore>();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
