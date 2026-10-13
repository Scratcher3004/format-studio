using FormatStudio.Importers;
using FormatStudio.Importers.Mojang;
using FormatStudio.Web;
using FormatStudio.Web.Components;
using MudBlazor.Services;

ImporterRegistry.Register(new MojangImport());

var builder = WebApplication.CreateBuilder(args);

// The project comes from --project <file|folder> (or FORMATSTUDIO_PROJECT), default: the current folder.
var projectPath = builder.Configuration["project"] ?? Environment.GetEnvironmentVariable("FORMATSTUDIO_PROJECT") ?? Directory.GetCurrentDirectory();

// A local tool started with "dotnet run": serve MudBlazor's assets from the build output in every environment.
builder.WebHost.UseStaticWebAssets();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddSingleton(new ProjectService(projectPath));

var app = builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
