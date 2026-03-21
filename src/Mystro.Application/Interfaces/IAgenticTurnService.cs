using Mystro.Domain.Entities;

namespace Mystro.Application.Interfaces;

public interface IAgenticTurnService
{
    Task<AgenticTurnObservation> ExecuteAgenticTurnAsync(string scriptBody);
}
