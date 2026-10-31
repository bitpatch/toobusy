namespace TooBusy.Core.Queue;

// The tracker refused to do what it was asked, or did not answer; the message says why, in words for the user.
// MayPass says that the failure may pass by itself, as when GitHub did not answer in time or the network was away:
// the same call may answer when it is made again. A failure that stays, as a task that does not exist or a token
// without the scope, does not pass however often the call is made.
public sealed class TrackerException(string message, bool mayPass = false) : Exception(message)
{
    public bool MayPass { get; } = mayPass;
}
