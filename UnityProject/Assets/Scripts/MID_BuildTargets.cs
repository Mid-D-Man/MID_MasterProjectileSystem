// Packages/com.midmanstudio.projectilesystem/Runtime/Core/MID_BuildTargets.cs
// Central build target detection. Import this namespace anywhere you need platform guards.

namespace MidManStudio.Projectiles
{
    public static class MID_BuildTargets
    {
#if UNITY_SERVER || MIDMAN_SERVER_BUILD
        public const bool IsServerBuild = true;
        public const bool IsClientBuild = false;
#else
        public const bool IsServerBuild = false;
        public const bool IsClientBuild = true;
#endif

#if UNITY_EDITOR
        public const bool IsEditor = true;
#else
        public const bool IsEditor = false;
#endif
    }
}
