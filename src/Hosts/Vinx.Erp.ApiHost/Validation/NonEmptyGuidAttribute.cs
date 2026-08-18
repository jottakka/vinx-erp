using System.ComponentModel.DataAnnotations;

namespace Vinx.Erp.ApiHost.Validation;

/// <summary>Exige GUID ausente apenas quando null; rejeita o valor padrão (vazio).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NonEmptyGuidAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext context) =>
        value is null || (value is Guid guid && guid != Guid.Empty)
            ? ValidationResult.Success
            : new ValidationResult(
                ErrorMessage ?? $"{context.DisplayName} must be a non-empty UUID."
            );
}
