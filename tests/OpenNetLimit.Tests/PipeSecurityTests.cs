using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenNetLimit.Service.IPC;
using Xunit;

namespace OpenNetLimit.Tests;

public class PipeSecurityTests
{
    [Fact]
    public void ServiceWithoutElevation_CanOpenTheNextPipeInstance()
    {
        var name = $"OpenNetLimit-test-{Guid.NewGuid():N}";

        using var first = PipeServer.CreateSecurePipe(name, firstInstance: true);
        using var second = PipeServer.CreateSecurePipe(name, firstInstance: false);

        Assert.NotNull(second);
    }

    [Fact]
    public void ServerAccount_MayCreateInstances()
    {
        using var self = WindowsIdentity.GetCurrent();

        Assert.True(Allows(PipeServer.BuildPipeSecurity(), self.User!, PipeAccessRights.CreateNewInstance));
    }

    [Fact]
    public void OtherUsers_CanTalkButNotServe()
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = PipeServer.BuildPipeSecurity();

        Assert.True(Allows(security, users, PipeAccessRights.ReadWrite));
        Assert.False(Allows(security, users, PipeAccessRights.CreateNewInstance));
    }

    private static bool Allows(PipeSecurity security, SecurityIdentifier who, PipeAccessRights rights) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                         rule.IdentityReference.Equals(who) &&
                         (rule.PipeAccessRights & rights) == rights);
}
