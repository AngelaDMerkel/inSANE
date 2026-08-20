using System.Runtime.InteropServices;

namespace InSane;

public static class RuntimeIdentity
{
    public static int? EffectiveUid => OperatingSystem.IsWindows() ? null : checked((int)GetEffectiveUserId());
    public static int? EffectiveGid => OperatingSystem.IsWindows() ? null : checked((int)GetEffectiveGroupId());

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint GetEffectiveGroupId();
}
