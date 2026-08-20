using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Vinx.Erp.ApiHost.Validation;

/// <summary>Valida limites decimais com cultura invariável no binding HTTP.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class DecimalRangeAttribute : ValidationAttribute
{
    public DecimalRangeAttribute(string minimum, string maximum)
    {
        Minimum = decimal.Parse(minimum, CultureInfo.InvariantCulture);
        Maximum = decimal.Parse(maximum, CultureInfo.InvariantCulture);
    }

    public decimal Minimum { get; }

    public decimal Maximum { get; }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
        value is decimal amount && amount >= Minimum && amount <= Maximum
            ? ValidationResult.Success
            : new ValidationResult(
                ErrorMessage
                    ?? $"{validationContext.DisplayName} must be between {Minimum} and {Maximum}."
            );
}
