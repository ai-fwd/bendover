using Mystro.Application.Interfaces;

namespace Mystro.Infrastructure.Services;

public class PromptOptRunContextAccessor : IPromptOptRunContextAccessor
{
    public PromptOptRunContext? Current { get; set; }
}
