namespace Bendover.Application.Interfaces;

public interface IEngineerBodyValidator
{
    EngineerBodyValidationResult Validate(string bodyContent);
}

public sealed record EngineerBodyValidationResult(bool IsValid, string? ErrorMessage)
{
    public static EngineerBodyValidationResult Success() => new(true, null);

    public static EngineerBodyValidationResult Failure(string errorMessage) => new(false, errorMessage);
}
