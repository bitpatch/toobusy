using TooBusy.Core.Doctor;
using TooBusy.Core.Setup;

namespace TooBusy.Cli;

// What the setup needs to know of this machine: the checks of `doctor` that need no settings, each failed one
// with its fix, whether GitHub can be asked, and the repository the project is cloned from.
public sealed class MachineEnvironment(ICheckupMachine machine, string? origin) : ISetupEnvironment
{
    public async Task<SetupEnvironment> InspectAsync(CancellationToken cancellationToken)
    {
        var checks = await Checkup.MachineAsync(machine, new NoCheckupView(), cancellationToken);
        return new SetupEnvironment(
            [.. checks.Where(check => check.State == CheckState.Failed).Select(check => new SetupProblem(check.Text, check.Fix ?? ""))],
            TrackerReachable: checks.Any(check => check is { Name: Checkup.TrackerLogin, State: CheckState.Passed }),
            origin);
    }
}
