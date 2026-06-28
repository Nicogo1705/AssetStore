// Copyright (c) <YEAR> <COPYRIGHT HOLDER>
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

namespace AssetStore.Core.Validation;

/// <summary>Severity of a validation message.</summary>
public enum ValidationSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>A single validation finding.</summary>
/// <param name="Severity">How serious the finding is.</param>
/// <param name="Code">Stable machine-readable code, e.g. <c>id.mismatch</c>.</param>
/// <param name="Text">Human-readable description.</param>
public sealed record ValidationMessage(ValidationSeverity Severity, string Code, string Text)
{
    public override string ToString() => $"[{Severity}] {Code}: {Text}";
}

/// <summary>Accumulates validation findings and derives an overall status.</summary>
public sealed class ValidationReport
{
    private readonly List<ValidationMessage> _messages = [];

    public IReadOnlyList<ValidationMessage> Messages => _messages;

    public bool HasErrors => _messages.Any(m => m.Severity == ValidationSeverity.Error);

    public bool HasWarnings => _messages.Any(m => m.Severity == ValidationSeverity.Warning);

    public void Add(ValidationSeverity severity, string code, string text) =>
        _messages.Add(new ValidationMessage(severity, code, text));

    public void Error(string code, string text) => Add(ValidationSeverity.Error, code, text);

    public void Warning(string code, string text) => Add(ValidationSeverity.Warning, code, text);

    /// <summary>Maps to <c>index-lock</c> <c>validationStatus</c>: ok / warning / error.</summary>
    public string Status => HasErrors ? "error" : HasWarnings ? "warning" : "ok";
}
