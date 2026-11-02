// Lets the EditMode tests call the controller-only members of the Internal Model view (for example,
// InternalModelView.SetBelievedPose) without making them public to algorithms.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RobotVacuum.Tests.EditMode")]
