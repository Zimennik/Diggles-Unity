namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    // C# 9 `init`-only setters and `record` types require this type to exist.
    // The.NET Framework profile Unity targets does not define it, so each
    // script assembly that uses init/record must supply its own internal copy.
    // (The Editor assembly has its own at Editor/Importers/IsExternalInit.cs;
    // this one covers the Runtime assembly after ThreeDbData/TgaLoader moved here.)
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
