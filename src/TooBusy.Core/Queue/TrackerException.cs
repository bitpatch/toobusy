namespace TooBusy.Core.Queue;

// The tracker refused to do what it was asked, or did not answer; the message says why, in words for the user.
public sealed class TrackerException(string message) : Exception(message);
