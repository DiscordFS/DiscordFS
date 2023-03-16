using System.Runtime.InteropServices;
using Vanara.InteropServices;
using static Vanara.PInvoke.CldApi;

namespace DiscordFS.Platforms.Windows.Storage;

public class SafePlaceholderList : SafeNativeArray<CF_PLACEHOLDER_CREATE_INFO>
{
    protected override void Dispose(bool disposing)
    {
        if (Elements != null)
        {
            foreach (var item in Elements)
            {
                if (item.FileIdentity != 0)
                {
                    Marshal.FreeCoTaskMem(item.FileIdentity);
                }
            }
        }

        base.Dispose(disposing);
    }
}