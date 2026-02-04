using Bendover.Application.Interfaces;

namespace Bendover.Infrastructure.Services;

public class PromptOptRunContextAccessor : IPromptOptRunContextAccessor
{
    public PromptOptRunContext? Current { get; set; }
}
