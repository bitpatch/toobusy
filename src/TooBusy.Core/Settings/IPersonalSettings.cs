using TooBusy.Core.Assistant;
using TooBusy.Core.Queue;

namespace TooBusy.Core.Settings;

// Where the choices of the user are kept for the project: the milestone to work on, the model and the effort the tasks
// are done with by default, and how many sessions work at once. Each is null until the user has chosen it.
public interface IPersonalSettings
{
    MilestoneChoice? LoadMilestone();

    void SaveMilestone(MilestoneChoice choice);

    ModelChoice? LoadModel();

    void SaveModel(ModelChoice choice);

    // One of the levels of effort the assistant has.
    string? LoadEffort();

    void SaveEffort(string effort);

    // How many sessions work at once, 1 or more: the limits they share are those of the account of the user. One that
    // is not chosen is one session.
    int? LoadSessions();

    void SaveSessions(int sessions);
}
