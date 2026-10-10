using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Settings;

// Where the choices of the user are kept for the project: the milestone to work on, the model and the effort the
// tasks are done with by default, and the share of the weekly limit a run may use. Each is null until the user has
// chosen it.
public interface IPersonalSettings
{
    MilestoneChoice? LoadMilestone();

    void SaveMilestone(MilestoneChoice choice);

    ModelChoice? LoadModel();

    void SaveModel(ModelChoice choice);

    // One of the levels of effort the assistant has.
    string? LoadEffort();

    void SaveEffort(string effort);

    // How much of the weekly limit of usage a run may use, in percent.
    int? LoadShare();

    void SaveShare(int share);
}
