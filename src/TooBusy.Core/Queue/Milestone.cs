namespace TooBusy.Core.Queue;

// An open milestone of the repository, with the number of its tasks that are still open.
public sealed record Milestone(string Title, DateOnly? Due, int OpenTasks);
