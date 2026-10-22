using FormatStudio.Natives;
using FormatStudio.Projects;

namespace FormatStudio.Minecraft;

/// <summary>
/// The <c>minecraft</c> library: native types for Minecraft's formats (NBT). A project uses them with
/// <c>dependencies: [minecraft]</c> once a host has called <see cref="Register"/>.
/// </summary>
public static class MinecraftLibrary
{
    public const string Name = "minecraft";

    /// <summary>The native types of the library.</summary>
    public static IReadOnlyList<INativeType> Natives { get; } = [NbtNative.Instance];

    public static HostLibrary Library { get; } = new()
    {
        Name = Name,
        Version = "1.0.0",
        Description = "Native types for Minecraft's formats: NBT",
        Natives = Natives,
        // nbt used to be built in. TODO(legacy-nbt): remove with HostLibraries.WithLegacyDependencies.
        LegacyNatives = ["nbt"],
    };

    /// <summary>Makes the library available to the projects that depend on it. Call once at start-up.</summary>
    public static void Register() => HostLibraries.Register(Library);

    /// <summary>The native types of the library as a set, for projects built in code without dependencies.</summary>
    public static NativeTypes NativeSet => NativeTypes.BuiltIn.With(Natives, (_, _) => { });
}
