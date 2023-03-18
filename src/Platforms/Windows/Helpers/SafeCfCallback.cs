using Serilog;
using static Vanara.PInvoke.CldApi;

namespace DiscordFS.Platforms.Windows.Helpers;

public class SafeCfCallback
{
    private readonly CF_CALLBACK _originalCallback;

    public SafeCfCallback(CF_CALLBACK originalCallback)
    {
        _originalCallback = originalCallback;
        GC.KeepAlive(this);
    }

    public void Callback(in CF_CALLBACK_INFO callbackInfo, in CF_CALLBACK_PARAMETERS callbackParameters)
    {
        try
        {
            _originalCallback(callbackInfo, callbackParameters);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while processing CF callback: " + _originalCallback.Method.Name);
        }
    }
}