using Bendover.SDK;

namespace Bendover.ScriptRunner;

public sealed class ScriptGlobals
{
    public ScriptGlobals()
    {
        sdk = new BendoverSDK();
    }

    public BendoverSDK sdk { get; }
}
