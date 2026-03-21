namespace Mystro.Application;

public class ScriptGenerator
{
    public string WrapCode(string rawCode)
    {
        // Wrap raw code in a .csx structure with references
        return $@"
#r ""Mystro.SDK.dll""
using Mystro.SDK;

{rawCode}
";
    }
}
