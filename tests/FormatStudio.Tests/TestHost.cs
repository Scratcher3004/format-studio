using System.Runtime.CompilerServices;
using FormatStudio.Minecraft;

namespace FormatStudio.Tests;

/// <summary>Registers what the command line and the web UI register at start-up.</summary>
internal static class TestHost
{
    [ModuleInitializer]
    public static void Register() => MinecraftLibrary.Register();
}
