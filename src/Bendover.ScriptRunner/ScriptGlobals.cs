using Bendover.SDK;

namespace Bendover.ScriptRunner;

public sealed class ScriptGlobals
{
    public ScriptGlobals(BendoverSDK sdk)
    {
        this.sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
    }

    public BendoverSDK sdk { get; }
}
