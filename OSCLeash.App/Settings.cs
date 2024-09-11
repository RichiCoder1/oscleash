using System.ComponentModel.DataAnnotations;
using System.Net;
using FluentValidation;

namespace OSCLeash.App;

internal sealed class Settings
{
    public string? IP { get; set; }
    [Range(0.01f, 10.0f)]
    public required float RunDeadzone { get; set; }
    [Range(0.01f, 10.0f)]
    public required float WalkDeadzone { get; set; }
    [Range(0.01f, 10.0f)]
    public required float StrengthMultiplier { get; set; }
    [Range(0.01f, 10.0f)]
    public required float UpDownCompensation { get; set; }
    [Range(0.01f, 10.0f)]
    public required float UpDownDeadzone { get; set; }
    public required bool TurningEnabled { get; set; }
    [Range(0.01f, 10.0f)]
    public required float TurningMultiplier { get; set; }
    [Range(0.01f, 10.0f)]
    public required float TurningDeadzone { get; set; }
    public required int TurningGoal { get; set; }
}

internal class SettingsValidator : AbstractValidator<Settings>
{
    public SettingsValidator()
    {
        RuleFor(setting => setting.IP).Must(ip => IPAddress.TryParse(ip, out var _)).WithMessage("IP must be a valid IP address.");
    }
}