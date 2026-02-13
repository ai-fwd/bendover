using Bendover.SDK;

namespace Bendover.ScriptRunner;

public sealed class ScriptGlobals
{
    public ScriptGlobals()
    {
        sdk = new SdkFacade(new BendoverSDK());
    }

    public SdkFacade sdk { get; }
}
