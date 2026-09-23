using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using AstralWarden.Nina.Beacon.Contracts;

namespace AstralWarden.Nina.Beacon.Instructions;

/// <summary>
/// Sequencer instruction: emit a user-authored alert through the Astral Warden pipeline. Put it
/// anywhere in a sequence (e.g. inside a condition branch) to get pushed a custom notification
/// when that point is reached. Observability only — it commands nothing.
/// </summary>
[ExportMetadata("Name", "Send Astral Warden alert")]
[ExportMetadata("Description", "Sends a custom alert to your phone via the Astral Warden agent when this instruction runs")]
[ExportMetadata("Icon", "ScriptSVG")]
[ExportMetadata("Category", "Astral Warden")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public class SendWardenAlertInstruction : SequenceItem, IValidatable
{
    private string _title = "Sequence alert";
    private string _severity = "info";
    private string _message = "";
    private IList<string> _issues = new List<string>();

    [ImportingConstructor]
    public SendWardenAlertInstruction() { }

    [JsonProperty]
    public string Title
    {
        get => _title;
        set { _title = value?.Trim() ?? ""; RaisePropertyChanged(); }
    }

    /// <summary>info | warn | error (anything else is treated as info downstream).</summary>
    [JsonProperty]
    public string Severity
    {
        get => _severity;
        set { _severity = value?.Trim().ToLowerInvariant() ?? "info"; RaisePropertyChanged(); }
    }

    [JsonProperty]
    public string Message
    {
        get => _message;
        set { _message = value?.Trim() ?? ""; RaisePropertyChanged(); }
    }

    public IList<string> Issues
    {
        get => _issues;
        set { _issues = value; RaisePropertyChanged(); }
    }

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        // A failed telemetry send must never fail the instruction — that would abort the user's
        // sequence over a monitoring detail.
        try
        {
            BeaconRuntime.Broadcast?.Invoke("alert.custom", new AlertPayload(
                string.IsNullOrWhiteSpace(Title) ? "Sequence alert" : Title,
                Severity is "warn" or "error" ? Severity : "info",
                string.IsNullOrWhiteSpace(Message) ? null : Message));
        }
        catch (Exception ex)
        {
            NINA.Core.Utility.Logger.Warning($"Beacon: custom alert not sent: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public bool Validate()
    {
        var issues = new List<string>();
        try
        {
            if (BeaconRuntime.Broadcast is null)
                issues.Add("Astral Warden Beacon is not running");
            else if (BeaconRuntime.ClientCount?.Invoke() == 0)
                issues.Add("Astral Warden agent is not connected (the alert would go nowhere)");
        }
        catch
        {
            // Validate runs on the sequencer UI path; report clean rather than throwing at it.
        }
        Issues = issues;
        return issues.Count == 0;
    }

    public override object Clone() => new SendWardenAlertInstruction
    {
        Title = Title,
        Severity = Severity,
        Message = Message,
        Icon = Icon,
        Name = Name,
        Category = Category,
        Description = Description,
    };
}
