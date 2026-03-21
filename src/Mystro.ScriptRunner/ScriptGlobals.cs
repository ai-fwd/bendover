using Mystro.SDK;

namespace Mystro.ScriptRunner;

public sealed class ScriptGlobals
{
    public ScriptGlobals(MystroSDK sdk)
    {
        this.sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
    }

    public MystroSDK sdk { get; }
}
