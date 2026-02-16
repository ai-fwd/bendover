using Bendover.Domain.Entities;

namespace Bendover.Application.Interfaces;

public interface IAgenticTurnService
{
    Task<AgenticTurnObservation> ExecuteAgenticTurnAsync(string scriptBody, AgenticTurnSettings settings);
}
