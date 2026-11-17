using RoomForge.Application.Reconstruction;

namespace RoomForge.UnitTests;

public sealed class SubmissionStatesTests
{
    [Fact]
    public void UnknownOutcomeCannotBeSubmittedAgainWithoutReconciliation()
    {
        Assert.True(SubmissionStates.RequiresReconciliationBeforeSubmit(SubmissionStates.OutcomeUnknown));
        Assert.False(SubmissionStates.CanTransition(SubmissionStates.OutcomeUnknown, SubmissionStates.InProgress));
        Assert.True(SubmissionStates.CanTransition(SubmissionStates.OutcomeUnknown, SubmissionStates.Accepted));
    }

    [Theory]
    [InlineData(SubmissionStates.NotAttempted, SubmissionStates.InProgress, true)]
    [InlineData(SubmissionStates.InProgress, SubmissionStates.OutcomeUnknown, true)]
    [InlineData(SubmissionStates.InProgress, SubmissionStates.Accepted, true)]
    [InlineData(SubmissionStates.Accepted, SubmissionStates.Running, true)]
    [InlineData(SubmissionStates.Running, SubmissionStates.Completed, true)]
    [InlineData(SubmissionStates.Completed, SubmissionStates.InProgress, false)]
    [InlineData(SubmissionStates.TerminalFailure, SubmissionStates.Accepted, false)]
    public void EnforcesDocumentedSubmissionTransitions(string current, string next, bool allowed) =>
        Assert.Equal(allowed, SubmissionStates.CanTransition(current, next));
}
